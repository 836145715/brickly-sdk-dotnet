using Brickly.Runtime.V1;
using Grpc.Core;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class InteractTests
{
    [Fact]
    public async Task InteractRequiresOnEvent()
    {
        FakeHost.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var error = await Assert.ThrowsAsync<BppException>(
            () => runtime.InteractAsync("live", null, new InteractOptions { OnEvent = null! }));
        Assert.Equal(BppErrorCodes.InvalidInput, error.Code);

        var callError = await Assert.ThrowsAsync<BppException>(
            () => runtime.CallAsync("live", null, new CallOptions { OnEvent = null! }));
        Assert.Equal(BppErrorCodes.InvalidInput, callError.Code);
    }

    [Fact]
    public async Task OpenedMustBeFirstServerFrame()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.OnEvent(_ => { });
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            using var call = client.RawInteract();
            await call.RequestStream.WriteAsync(new ClientFrame
            {
                Header = new FrameHeader { Sequence = 1 },
                Event = new EventFrame { Payload = BrickValueCodec.FromClr("oops") },
            });
            var error = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                await call.ResponseStream.MoveNext();
            });
            Assert.Equal(StatusCode.Internal, error.StatusCode);
            Assert.Contains("PROTOCOL_VIOLATION", error.Status.Detail);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SequenceViolationClosesInputAndFinishes()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.OnEvent(_ => { });
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var session = await client.OpenInteractAsync("live", null);
            await session.WriteRawAsync(new ClientFrame
            {
                Header = new FrameHeader { Sequence = 5 },
                Event = new EventFrame { Payload = BrickValueCodec.FromClr("bad-sequence") },
            });
            var final = await session.WaitForFinalAsync();
            Assert.Equal("closed", BrickValueCodec.ToClr(final.Final.Result));
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleRequestsRoundTrip()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.HandleRequests((request, _) =>
                {
                    var value = Convert.ToInt64(request);
                    return Task.FromResult<object?>(value + 1);
                });
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var session = await client.OpenInteractAsync("live", null);
            var answer = await session.RequestAsync(41);
            Assert.Equal(42L, Convert.ToInt64(answer));
            await session.CloseInputAsync();
            await session.WaitForFinalAsync();
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleRequestsRejectsSecondRegistration()
    {
        Exception? captured = null;
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.HandleRequests((_, _) => Task.FromResult<object?>(null));
                try
                {
                    ctx.HandleRequests((_, _) => Task.FromResult<object?>(null));
                }
                catch (Exception error)
                {
                    captured = error;
                }
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var session = await client.OpenInteractAsync("live", null);
            await session.CloseInputAsync();
            await session.WaitForFinalAsync();
            Assert.Equal(BppErrorCodes.ProtocolError, Assert.IsType<BppException>(captured).Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendEventDeliversToOnEvent()
    {
        var received = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.OnEvent(value => received.TrySetResult(value));
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var session = await client.OpenInteractAsync("live", new { prompt = "hi" });
            await session.SendEventAsync(new { chunk = "abc" });
            var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var map = Assert.IsType<Dictionary<string, object?>>(value);
            Assert.Equal("abc", map["chunk"]);
            await session.CloseInputAsync();
            await session.WaitForFinalAsync();
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelRequestStopsOnlyOneRequest()
    {
        var host = await FakeHost.StartAsync();
        host.ConnectorInteractRequestHandler = payload =>
        {
            if (BrickValueCodec.ToClr(payload) is string text && text == "a")
            {
                return new TaskCompletionSource<BrickValue>().Task;
            }
            return Task.FromResult(BrickValueCodec.FromClr(42L));
        };
        host.ConnectorInteractFinalResult = () => BrickValueCodec.FromClr("ended");
        host.ApplyEnvironment("""{"openai":{"brickId":"com.brickly.openai","origin":"installed","version":"2.1.0"}}""");
        var runtime = new BricklyRuntime().OnCommand("caller", async (ctx, _) =>
        {
            var dependency = await ctx.Dependencies().Require("openai").StartAsync();
            return await RunSessionAsync(dependency);
        });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("caller", null);
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.True((bool)value["cancelled"]!);
            Assert.Equal(42L, Convert.ToInt64(value["answer"]));
            Assert.NotEmpty(host.ConnectorSessions);
            Assert.NotEmpty(host.ConnectorSessions[0].Cancels);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task CallAsyncEndsAndReturnsFinal()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        host.PlatformInteractFinalResult = () => BrickValueCodec.FromClr("final-value");
        try
        {
            var result = await runtime.CallAsync(
                "assist",
                new { prompt = "x" },
                new CallOptions { OnEvent = _ => { } });
            Assert.Equal("final-value", result);
            Assert.True(host.PlatformSessions.Count > 0);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeInteractAndCallOutsideCommandRequireHost()
    {
        FakeHost.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var interactError = await Assert.ThrowsAsync<BppException>(
            () => runtime.InteractAsync("live", null, new InteractOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, interactError.Code);
        var callError = await Assert.ThrowsAsync<BppException>(
            () => runtime.CallAsync("live", null, new CallOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, callError.Code);
    }

    [Fact]
    public async Task IdleInteractSurvivesHostKeepalive()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("live", (ctx, _) =>
            {
                ctx.HandleRequests((request, _) => Task.FromResult<object?>(request));
                return WaitClosedAsync(ctx);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var session = await client.OpenInteractAsync("live", null);
            await Task.Delay(1200);
            var answer = await session.RequestAsync("ping");
            Assert.Equal("ping", answer);
            await session.CloseInputAsync();
            await session.WaitForFinalAsync();
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    private static async Task<object?> RunSessionAsync(StartedToolHandle dependency)
    {
        var cancelled = false;
        var session = await dependency.InteractAsync(
            "chat",
            new Dictionary<string, object?> { ["prompt"] = "hello" },
            new InteractOptions { OnEvent = _ => { } });
        using var requestCts = new CancellationTokenSource();
        var pending = session.RequestAsync("a", requestCts.Token);
        await Task.Delay(80);
        requestCts.Cancel();
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        var answer = await session.RequestAsync("b");
        await session.EndAsync();
        await dependency.DisposeAsync();
        return new Dictionary<string, object?>
        {
            ["cancelled"] = cancelled,
            ["answer"] = answer,
        };
    }

    private static async Task<object?> WaitClosedAsync(CommandContext ctx)
    {
        await ctx.Closed;
        return "closed";
    }
}
