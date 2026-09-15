using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class InteractTests
{
    private const string Bindings =
        """{"openai":{"brickId":"com.brickly.openai","origin":"installed","version":"2.1.0"}}""";

    [Fact]
    public async Task InteractRequiresOnEvent()
    {
        TestHostProcess.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var error = await Assert.ThrowsAsync<BppException>(
            () => runtime.InteractAsync("live", null, new InteractOptions { OnEvent = null! }));
        Assert.Equal(BppErrorCodes.InvalidInput, error.Code);

        var callError = await Assert.ThrowsAsync<BppException>(
            () => runtime.CallAsync("live", null, new CallOptions { OnEvent = null! }));
        Assert.Equal(BppErrorCodes.InvalidInput, callError.Code);
    }

    /// <summary>首帧非 open → 真 runtime 判 PROTOCOL_VIOLATION 并流级报错。</summary>
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
            var reply = await host.InteractScriptAsync(new
            {
                open = false,
                steps = new object[]
                {
                    new { @event = "oops" },
                    new { expect = new { kind = "error" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal("error", reply.State);
            var error = Assert.Single(reply.Received!, item => item.Kind == "error");
            Assert.Contains("PROTOCOL_VIOLATION", error.Error);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    /// <summary>sequence 跳号 → runtime 关闭输入侧，handler 走完返回 final。</summary>
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
            var reply = await host.InteractScriptAsync(new
            {
                commandId = "live",
                steps = new object[]
                {
                    // open 后 runtime 先回 opened 握手帧，expect 按序消费
                    new { expect = new { kind = "opened" } },
                    new { raw = new { sequence = 5, @event = "bad-sequence" } },
                    new { expect = new { kind = "final" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            var final = Assert.Single(reply.Received!, item => item.Kind == "final");
            Assert.Equal("closed", final.Final.GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
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
            var reply = await host.InteractScriptAsync(new
            {
                commandId = "live",
                steps = new object[]
                {
                    new { expect = new { kind = "opened" } },
                    new { request = 41L, @as = "a" },
                    new { expect = new { kind = "response" } },
                    new { halfClose = true },
                    new { expect = new { kind = "final" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            var response = Assert.Single(reply.Received!, item => item.Kind == "response");
            Assert.Equal(42L, response.Response.GetProperty("value").GetInt64());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
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
            var reply = await host.InteractScriptAsync(new
            {
                commandId = "live",
                steps = new object[]
                {
                    new { expect = new { kind = "opened" } },
                    new { halfClose = true },
                    new { expect = new { kind = "final" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            Assert.Equal(BppErrorCodes.ProtocolError, Assert.IsType<BppException>(captured).Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
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
            var reply = await host.InteractScriptAsync(new
            {
                commandId = "live",
                input = new { prompt = "hi" },
                steps = new object[]
                {
                    new { expect = new { kind = "opened" } },
                    new { @event = new { chunk = "abc" } },
                    new { halfClose = true },
                    new { expect = new { kind = "final" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var map = Assert.IsType<Dictionary<string, object?>>(value);
            Assert.Equal("abc", map["chunk"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    /// <summary>
    /// 依赖 interact 的中途 cancel_request：经真宿主 connector 内核路由到
    /// 第二个真 runtime（com.brickly.openai），只取消挂起的那一个 request。
    /// </summary>
    [Fact]
    public async Task CancelRequestStopsOnlyOneRequest()
    {
        var host = TestHostProcess.TryStart()
            ?? throw new InvalidOperationException("真宿主测试工件不可用");
        var spawnCaller = await host.SpawnAsync("com.brickly.test-app");
        var spawnDep = await host.SpawnAsync(
            "com.brickly.openai", origin: "installed", version: "2.1.0");
        await host.EnableDependencyKernelAsync(["chat"]);
        try
        {
            // 依赖侧真 runtime：'a' 挂起直到 request ct 取消（协议约定 handler 必须响应 ct，
            // 裸 Task 不响应会把 dep 侧 SettleRequests 卡死），其余回 42
            host.ApplyEnvironment(spawnDep);
            await using var depRuntime = new BricklyRuntime().OnCommand("chat", (ctx, _) =>
            {
                ctx.HandleRequests(async (request, ct) =>
                {
                    if (Equals(request, "a"))
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                        return null;
                    }
                    return 42L;
                });
                return WaitClosedAsync(ctx);
            });
            await depRuntime.StartAsync();

            host.ApplyEnvironment(spawnCaller, dependencyBindings: Bindings);
            await using var runtime = new BricklyRuntime().OnCommand("caller", async (ctx, _) =>
            {
                var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                return await RunSessionAsync(dependency);
            });
            await runtime.StartAsync();

            using var client = new RuntimeClient(
                await host.RuntimeEndpointAsync(registerIndex: 1), spawnCaller.HostToRuntimeToken);
            var result = await client.InvokeAsync("caller", null);
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.True((bool)value["cancelled"]!);
            Assert.Equal(42L, Convert.ToInt64(value["answer"]));

            var interact = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Interact", StringComparison.Ordinal));
            Assert.True(interact.Frames >= 4, $"open+request×2+cancel 至少 4 帧，实际 {interact.Frames}");
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task CallAsyncEndsAndReturnsFinal()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        await host.SetPlatformResponseAsync("$interact", "final-value");
        try
        {
            var result = await runtime.CallAsync(
                "assist",
                new { prompt = "x" },
                new CallOptions { OnEvent = _ => { } });
            Assert.Equal("final-value", result);
            var interact = await host.WaitCallAsync(call =>
                call.Path.EndsWith("PlatformService/Interact", StringComparison.Ordinal));
            Assert.Equal("call", interact.Intent);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeInteractAndCallOutsideCommandRequireHost()
    {
        TestHostProcess.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var interactError = await Assert.ThrowsAsync<BppException>(
            () => runtime.InteractAsync("live", null, new InteractOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, interactError.Code);
        var callError = await Assert.ThrowsAsync<BppException>(
            () => runtime.CallAsync("live", null, new CallOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, callError.Code);
    }

    /// <summary>宿主侧 1.2s 静默后 request 仍正常应答（保活不打断空闲会话）。</summary>
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
            var reply = await host.InteractScriptAsync(new
            {
                commandId = "live",
                steps = new object[]
                {
                    new { expect = new { kind = "opened" } },
                    new { sleepMs = 1200 },
                    new { request = "ping", @as = "p" },
                    new { expect = new { kind = "response" } },
                    new { halfClose = true },
                    new { expect = new { kind = "final" } },
                },
            });
            Assert.True(reply.Ok, reply.Error);
            var response = Assert.Single(reply.Received!, item => item.Kind == "response");
            Assert.Equal("ping", response.Response.GetProperty("value").GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
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
