using System.Text.Json;
using Grpc.Core;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class InvokeTests
{
    [Fact]
    public async Task InvokeDispatchesToHandlerAndReturnsResult()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("hello", (_, input) =>
            {
                var name = input.GetProperty("name").GetString();
                return Task.FromResult<object?>(new Dictionary<string, object?> { ["message"] = "Hello, " + name });
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("hello", new { name = "Brickly" });
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.Equal("Hello, Brickly", value["message"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvokeUnknownCommandReturnsCommandNotFound()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("hello", (_, _) => Task.FromResult<object?>(null)));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var error = await Assert.ThrowsAsync<RpcException>(() => client.InvokeAsync("nope", null));
            var brick = BrickErrorStatus.TryReadBrickError(error);
            Assert.NotNull(brick);
            Assert.Equal("COMMAND_NOT_FOUND", brick!.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task EchoCommandReturnsInput()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("echo", new { value = 7 });
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.Equal(7L, value["value"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task BppExceptionKeepsCode()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("fail", (_, _) => throw new BppException("INVALID_INPUT", "bad input")));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var error = await Assert.ThrowsAsync<RpcException>(() => client.InvokeAsync("fail", null));
            Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
            var brick = BrickErrorStatus.TryReadBrickError(error);
            Assert.Equal("INVALID_INPUT", brick!.Code);
            Assert.Equal("bad input", brick.Message);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlainExceptionMapsToInternalError()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("boom", (_, _) => throw new InvalidOperationException("kaboom")));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var error = await Assert.ThrowsAsync<RpcException>(() => client.InvokeAsync("boom", null));
            var brick = BrickErrorStatus.TryReadBrickError(error);
            Assert.Equal("INTERNAL", brick!.Code);
            Assert.DoesNotContain("token", brick.Message);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task CommandContextInvocationDefaultsToUnknown()
    {
        CommandInvocationContext? captured = null;
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("who", (ctx, _) =>
            {
                captured = ctx.Invocation;
                return Task.FromResult<object?>(null);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("who", null);
            Assert.NotNull(captured);
            Assert.Equal("unknown", captured!.Source);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnarySendAndOnEventRejected()
    {
        Exception? sendError = null;
        Exception? onEventError = null;
        Exception? handleRequestsError = null;
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("unary", (ctx, _) =>
            {
                try
                {
                    ctx.SendAsync("x").GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    sendError = error;
                }
                try
                {
                    ctx.OnEvent(_ => { });
                }
                catch (Exception error)
                {
                    onEventError = error;
                }
                try
                {
                    ctx.HandleRequests((_, _) => Task.FromResult<object?>(null));
                }
                catch (Exception error)
                {
                    handleRequestsError = error;
                }
                return Task.FromResult<object?>(null);
            }));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("unary", null);
            Assert.Equal(BppErrorCodes.ProtocolError, Assert.IsType<BppException>(sendError).Code);
            Assert.Equal(BppErrorCodes.ProtocolError, Assert.IsType<BppException>(onEventError).Code);
            Assert.Equal(BppErrorCodes.ProtocolError, Assert.IsType<BppException>(handleRequestsError).Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeInvokeOutsideCommandRequiresHost()
    {
        FakeHost.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var error = await Assert.ThrowsAsync<BppException>(() => runtime.InvokeAsync("hello", null));
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
    }

    [Fact]
    public void BrickValueRejectsUnsafeNumbersDepthAndNodeCount()
    {
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(1L << 60));
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(double.NaN));
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(double.PositiveInfinity));
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(-0.0d));
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(ulong.MaxValue));
    }

    [Fact]
    public void BrickValueRejectsDepthOverLimit()
    {
        object? nested = "leaf";
        for (var i = 0; i < 70; i++)
        {
            nested = new Dictionary<string, object?> { ["child"] = nested };
        }
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(nested));
    }

    [Fact]
    public void BrickValueRejectsNodeCountOverLimit()
    {
        var items = new List<object?>(100_001);
        for (var i = 0; i < 100_001; i++)
        {
            items.Add(i);
        }
        Assert.Throws<BppException>(() => BrickValueCodec.FromClr(items));
    }

    [Fact]
    public void BrickValueRoundTripsSafeIntegersAndStrings()
    {
        var value = BrickValueCodec.FromClr(new Dictionary<string, object?>
        {
            ["n"] = 42L,
            ["s"] = "hello",
            ["b"] = true,
            ["list"] = new List<object?> { 1L, 2L },
        });
        var element = BrickValueCodec.ToJsonElement(value);
        Assert.Equal(42L, element.GetProperty("n").GetInt64());
        Assert.Equal("hello", element.GetProperty("s").GetString());
        Assert.True(element.GetProperty("b").GetBoolean());
        Assert.Equal(2, element.GetProperty("list").GetArrayLength());
    }
}
