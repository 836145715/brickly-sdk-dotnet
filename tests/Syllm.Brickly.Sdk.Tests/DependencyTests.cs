using System.Text.Json;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class DependencyTests
{
    private const string Bindings =
        """{"openai":{"brickId":"com.brickly.openai","origin":"installed","version":"2.1.0"}}""";

    private const string TwoBindings =
        """{"openai":{"brickId":"com.brickly.openai","origin":"installed","version":"2.1.0"},"review_tool":{"brickId":"com.brickly.review","origin":"review","version":"1.3.0"}}""";

    [Fact]
    public async Task DependencyBindingsFromEnvAreReadOnlyAndIsolated()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: TwoBindings);
        try
        {
            var bindings = runtime.Dependencies.Bindings;
            Assert.Equal(2, bindings.Count);
            Assert.Equal(BrickOrigin.Installed, bindings["openai"].Origin);
            Assert.Equal(BrickOrigin.Review, bindings["review_tool"].Origin);

            bindings["openai"] = new BrickRef("com.example.other", BrickOrigin.Development, "9.9.9");
            Assert.Equal("com.brickly.openai", runtime.Dependencies.Bindings["openai"].BrickId);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RequireRejectsInvalidOrUndeclaredAlias()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var invalid = Assert.Throws<BppException>(() => runtime.Dependencies.Require("BadAlias"));
            Assert.Equal(BppErrorCodes.InvalidInput, invalid.Code);

            var undeclared = Assert.Throws<BppException>(() => runtime.Dependencies.Require("missing"));
            Assert.Equal(BppErrorCodes.DependencyNotDeclared, undeclared.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartOutsideCommandRequiresParent()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var error = await Assert.ThrowsAsync<BppException>(
                () => runtime.Dependencies.Require("openai").StartAsync());
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    /// <summary>
    /// 命令内 start → invoke → interact → dispose / stop 全链路：
    /// connector 内核把调用路由到第二个真 runtime（com.brickly.openai）。
    /// </summary>
    [Fact]
    public async Task StartedHandleInvokeInteractDisposeStop()
    {
        var fixture = await StartDependencyFixtureAsync(
            configureCaller: runtime => runtime
                .OnCommand("dispose-case", async (ctx, _) =>
                {
                    var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                    var value = await dependency.InvokeAsync("chat", new { prompt = "x" });
                    var session = await dependency.InteractAsync(
                        "chat",
                        null,
                        new InteractOptions { OnEvent = _ => { } });
                    var final = await session.EndAsync();
                    await dependency.DisposeAsync();
                    return new Dictionary<string, object?> { ["value"] = value, ["final"] = final };
                })
                .OnCommand("stop-case", async (ctx, _) =>
                {
                    var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                    await dependency.StopAsync();
                    return null;
                }),
            depChat: (interactResult: "end-ok", invokeResult: "chat-ok"));
        var (host, runtime, depRuntime, callerSpawn) = fixture;
        try
        {
            using var client = new RuntimeClient(
                await host.RuntimeEndpointAsync(registerIndex: 1), callerSpawn.HostToRuntimeToken);
            var result = await client.InvokeAsync("dispose-case", null);
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.Equal("chat-ok", value["value"]);
            Assert.Equal("end-ok", value["final"]);

            var invoke = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Invoke", StringComparison.Ordinal));
            var handleId = invoke.Request.GetProperty("handleId").GetString();
            Assert.False(string.IsNullOrEmpty(handleId));
            var dispose = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Dispose", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("handleId", out var id) &&
                id.GetString() == handleId &&
                call.Request.TryGetProperty("stop", out var stop) &&
                stop.GetBoolean() == false);

            await client.InvokeAsync("stop-case", null);
            await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Dispose", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("stop", out var stop) &&
                stop.GetBoolean());
        }
        finally
        {
            await runtime.DisposeAsync();
            await depRuntime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DependencyInvokeInsideCommandCarriesInvocationId()
    {
        var fixture = await StartDependencyFixtureAsync(
            configureCaller: runtime => runtime.OnCommand("caller", async (ctx, _) =>
            {
                var dependency = ctx.Dependencies().Require("openai");
                return await dependency.InvokeAsync("chat", new { prompt = "hi" });
            }),
            depChat: (interactResult: "unused", invokeResult: "ok"));
        var (host, runtime, depRuntime, callerSpawn) = fixture;
        try
        {
            using var client = new RuntimeClient(
                await host.RuntimeEndpointAsync(registerIndex: 1), callerSpawn.HostToRuntimeToken);
            await client.InvokeAsync("caller", null, invocationId: "inv-dep");
            var invoke = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Invoke", StringComparison.Ordinal));
            Assert.Equal("inv-dep", invoke.InvocationId);
        }
        finally
        {
            await runtime.DisposeAsync();
            await depRuntime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartInCommandSendsInvocationId()
    {
        var fixture = await StartDependencyFixtureAsync(
            configureCaller: runtime => runtime.OnCommand("starter", async (ctx, _) =>
            {
                var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                await dependency.DisposeAsync();
                return null;
            }),
            depChat: null);
        var (host, runtime, depRuntime, callerSpawn) = fixture;
        try
        {
            using var client = new RuntimeClient(
                await host.RuntimeEndpointAsync(registerIndex: 1), callerSpawn.HostToRuntimeToken);
            await client.InvokeAsync("starter", null, invocationId: "inv-start");
            var start = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Start", StringComparison.Ordinal));
            Assert.Equal("inv-start", start.InvocationId);
        }
        finally
        {
            await runtime.DisposeAsync();
            await depRuntime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task DependencyInteractOutsideCommandRequiresHost()
    {
        TestHostProcess.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        runtime.Dependencies.ReplaceFromJson(Bindings);
        var error = await Assert.ThrowsAsync<BppException>(
            () => runtime.Dependencies.Require("openai").InteractAsync(
                "chat",
                null,
                new InteractOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
    }

    [Fact]
    public async Task DependencyCallUsesCallIntent()
    {
        var fixture = await StartDependencyFixtureAsync(
            configureCaller: runtime => runtime.OnCommand("caller", async (ctx, _) =>
            {
                var dependency = ctx.Dependencies().Require("openai");
                return await dependency.CallAsync(
                    "chat",
                    new { prompt = "x" },
                    new CallOptions { OnEvent = _ => { } });
            }),
            depChat: (interactResult: "called", invokeResult: "unused"));
        var (host, runtime, depRuntime, callerSpawn) = fixture;
        try
        {
            using var client = new RuntimeClient(
                await host.RuntimeEndpointAsync(registerIndex: 1), callerSpawn.HostToRuntimeToken);
            var result = await client.InvokeAsync("caller", null);
            Assert.Equal("called", BrickValueCodec.ToClr(result.Result));
            var interact = await host.WaitCallAsync(call =>
                call.Path.EndsWith("BrickConnectorService/Interact", StringComparison.Ordinal));
            Assert.Equal("call", interact.Intent);
        }
        finally
        {
            await runtime.DisposeAsync();
            await depRuntime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartInEventScopeRejected()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var captured = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = runtime.Events.On("my-brick:tick", (_, _) =>
            {
                try
                {
                    runtime.Dependencies.Require("openai").StartAsync().GetAwaiter().GetResult();
                    captured.TrySetResult(null);
                }
                catch (Exception error)
                {
                    captured.TrySetResult(error);
                }
            });
            await host.WaitCallAsync(call =>
                call.Path.EndsWith("EventService/Subscribe", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("topic", out var t) &&
                t.GetString() == "my-brick:tick", timeoutMs: 15_000);

            await host.PushEventAsync(
                host.LastSpawn!.SpawnId,
                "my-brick:tick",
                new Dictionary<string, object?> { ["n"] = 1L });
            var error = await captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var bpp = Assert.IsType<BppException>(error);
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, bpp.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StartInCommandWithoutHostIsProtocol()
    {
        TestHostProcess.ClearEnvironment();
        await using var runtime = new BricklyRuntime().OnCommand("start-without-host", async (ctx, _) =>
        {
            await ctx.Dependencies().Require("openai").StartAsync();
            return null;
        });
        runtime.Dependencies.ReplaceFromJson(Bindings);
        using var input = JsonDocument.Parse("null");
        var error = await Assert.ThrowsAsync<BppException>(
            () => ((ICommandDispatcher)runtime).DispatchInvokeAsync(
                "start-without-host",
                input.RootElement,
                null,
                CancellationToken.None));
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
    }

    /// <summary>
    /// 双 runtime 装配：spawn 依赖 brick（com.brickly.openai installed/2.1.0）
    /// + 调用方 brick；先起依赖 runtime 再起调用方（Register 录制下标 0/1）。
    /// depChat: null 表示依赖 runtime 不注册命令。
    /// </summary>
    private static async Task<(
        TestHostProcess Host,
        BricklyRuntime Runtime,
        BricklyRuntime DepRuntime,
        TestHostProcess.SpawnInfo CallerSpawn)> StartDependencyFixtureAsync(
        Action<BricklyRuntime> configureCaller,
        (object? interactResult, object? invokeResult)? depChat)
    {
        var host = TestHostProcess.TryStart()
            ?? throw new InvalidOperationException("真宿主测试工件不可用（需要 node + @syllm/brickly-test-host）");
        try
        {
            var callerSpawn = await host.SpawnAsync("com.brickly.test-app");
            var depSpawn = await host.SpawnAsync(
                "com.brickly.openai", origin: "installed", version: "2.1.0");
            // 合成能力 brick 须声明命令才是"可调用能力"（hasCommands=false 时
            // connector.start 拒目标）；depRuntime 是否真注册 chat 不影响 start 路由
            await host.EnableDependencyKernelAsync("chat");

            host.ApplyEnvironment(depSpawn);
            var depRuntime = new BricklyRuntime();
            if (depChat is { } chat)
            {
                depRuntime.OnCommand("chat", (ctx, _) =>
                {
                    // interact 模式可注册 OnEvent；unary 下抛 ProtocolError → 回 invoke 罐头值
                    try
                    {
                        ctx.OnEvent(_ => { });
                        return Task.FromResult<object?>(chat.interactResult);
                    }
                    catch (BppException)
                    {
                        return Task.FromResult<object?>(chat.invokeResult);
                    }
                });
            }
            await depRuntime.StartAsync();

            host.ApplyEnvironment(callerSpawn, dependencyBindings: Bindings);
            var runtime = new BricklyRuntime();
            configureCaller(runtime);
            await runtime.StartAsync();
            return (host, runtime, depRuntime, callerSpawn);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }
}
