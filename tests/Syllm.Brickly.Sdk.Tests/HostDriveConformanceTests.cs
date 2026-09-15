using System.Text.Json;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

/// <summary>
/// 真宿主 host→runtime 驱动面 conformance（需 @syllm/brickly-test-host ≥0.12.0）。
/// 控制面 /spawn 铸凭据 → 环境变量注入 → 真 BricklyRuntime 进程内启动并注册 →
/// 覆盖 /invoke、批量 /interact、/events/push、/ui-handler + /ui-calls、/runtimes。
/// 工件未装或无 node 时跳过（与 HostConformanceTests 同策略）。
/// </summary>
public class HostDriveConformanceTests
{
    [Fact]
    public async Task RealHostDriveSurface()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return; // 未安装 @syllm/brickly-test-host 或无 node：跳过
        }

        var spawn = await host.SpawnAsync("drive-test");

        // interact 用例：handler 推一条事件帧后返回输入回声
        var runtime = new BricklyRuntime();
        runtime.OnCommand("interact-echo", async (ctx, input) =>
        {
            await ctx.SendAsync(new Dictionary<string, object?> { ["progress"] = 1 });
            return new Dictionary<string, object?> { ["echo"] = input.GetProperty("n").GetInt32() };
        });
        runtime.OnCommand("open-window", async (ctx, _) =>
        {
            try
            {
                var window = await ctx.UI().CreateBrowserWindowAsync("https://example.com");
                return window.WindowKey;
            }
            catch (Exception error)
            {
                return $"UI_FAIL:{error.GetType().Name}:{error.Message}";
            }
        });
        runtime.OnCommand("boom", (_, _) =>
            throw new BppException("DRIVE_CUSTOM", "drive boom"));

        var received = new TaskCompletionSource<object?>();
        runtime.Events.On("drive:ping", (payload, _) => received.TrySetResult(payload));

        ApplyEnvironment(host, spawn);
        try
        {
            await runtime.StartAsync();
            Assert.NotNull(runtime.RuntimeHandleId);

            // /runtimes 应能看到已注册句柄
            var runtimes = await host.RuntimesAsync();
            Assert.Contains(
                runtimes.GetProperty("runtimes").EnumerateArray(),
                item => item.GetProperty("spawnId").GetString() == spawn.SpawnId);

            // /invoke 成功路径（内置 echo 命令回显输入）
            var echo = await host.InvokeAsync(
                spawn.SpawnId, "echo", new Dictionary<string, object?> { ["n"] = 5 });
            Assert.True(echo.Ok, $"invoke 失败: {echo.Error?.Message}");
            Assert.Equal(5, echo.Result.GetProperty("n").GetInt32());

            // /invoke 错误路径：结构化 brick 错误码（不经 500）
            var failed = await host.InvokeAsync(spawn.SpawnId, "boom", null);
            Assert.False(failed.Ok);
            Assert.Equal("DRIVE_CUSTOM", failed.Error?.BrickCode);
            Assert.Contains("drive boom", failed.Error?.Message ?? string.Empty);

            // 批量 /interact：事件帧 + 最终结果
            var interact = await host.InteractAsync(
                spawn.SpawnId, "interact-echo", new Dictionary<string, object?> { ["n"] = 7 });
            Assert.True(interact.Ok, $"interact 失败: {interact.Error?.Message}");
            Assert.Equal(7, interact.Result.GetProperty("echo").GetInt32());
            var progress = Assert.Single(interact.Events ?? []);
            Assert.Equal(1, progress.GetProperty("progress").GetInt32());

            // /events/push → Runtime.Events 订阅收到 domain event
            await host.PushEventAsync(
                spawn.SpawnId, "drive:ping", new Dictionary<string, object?> { ["hello"] = "world" });
            var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("world", JsonSerializer.Serialize(payload));

            // /ui-handler 罐头响应 + /ui-calls 录制
            await host.SetUiResponseAsync(
                "createBrowserWindow",
                new
                {
                    windowKey = "w1",
                    windowId = 42,
                    webContentsId = 7,
                    url = "https://example.com",
                });
            var opened = await host.InvokeAsync(spawn.SpawnId, "open-window", null);
            Assert.True(opened.Ok, $"open-window 失败: {opened.Error?.Message}");
            Assert.Equal("w1", opened.Result.GetString());
            Assert.Contains(await host.UiCallsAsync(), call => call.Method == "createBrowserWindow");
        }
        finally
        {
            await runtime.DisposeAsync();
            ClearEnvironment();
        }
    }

    /// <summary>
    /// dependency.command_scoped_start：命令内 start 依赖 → handle.Invoke 经真 connector
    /// 路由到目标 runtime；命令外 start 被 SDK scope 断言拒绝。
    /// </summary>
    // @capability dependency.command_scoped_start
    [Fact]
    public async Task CommandScopedDependencyStart()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return;
        }

        await host.EnableDependencyKernelAsync("echo");
        var dep = await host.SpawnAsync(
            "acme.echo-dep", instanceId: "dep-1", origin: "installed", version: "1.2.3");
        var caller = await host.SpawnAsync("com.acme.caller", instanceId: "caller-1");

        // 依赖目标 runtime：echo 回显输入
        var depRuntime = new BricklyRuntime();
        depRuntime.OnCommand("echo", (_, input) =>
            Task.FromResult<object?>(new Dictionary<string, object?> { ["dep"] = input }));
        ApplyEnvironment(host, dep);
        await depRuntime.StartAsync();

        // 调用方 runtime：BRICKLY_DEPENDENCY_BINDINGS 声明 echo-dep → acme.echo-dep
        var callerRuntime = new BricklyRuntime();
        callerRuntime.OnCommand("use-dep", async (ctx, _) =>
        {
            var handle = await ctx.Dependencies().Require("echo-dep").StartAsync();
            var value = await handle.InvokeAsync(
                "echo", new Dictionary<string, object?> { ["x"] = 1 });
            await handle.DisposeAsync();
            return new Dictionary<string, object?> { ["dep"] = value };
        });
        callerRuntime.OnCommand("dep-direct", (ctx, _) =>
            ctx.Dependencies().Require("echo-dep").InvokeAsync(
                "echo", new Dictionary<string, object?> { ["y"] = 2 }));
        ApplyEnvironment(host, caller);
        Environment.SetEnvironmentVariable(
            "BRICKLY_DEPENDENCY_BINDINGS",
            """{"echo-dep":{"brickId":"acme.echo-dep","origin":"installed","version":"1.2.3"}}""");
        try
        {
            await callerRuntime.StartAsync();

            // 命令外 start：SDK 侧 scope 断言直接拒绝
            var denied = await Assert.ThrowsAsync<BppException>(
                () => callerRuntime.Dependencies.Require("echo-dep").StartAsync());
            Assert.Equal("PARENT_INVOCATION_REQUIRED", denied.Code);

            // start → handle.InvokeAsync → dispose：经 connector.startForCall + ToolHandle
            var used = await host.InvokeAsync(caller.SpawnId, "use-dep", null);
            Assert.True(used.Ok, $"use-dep 失败: {used.Error?.Message}");
            Assert.Equal(1, used.Result.GetProperty("dep").GetProperty("dep").GetProperty("x").GetInt32());

            // 无 handle 直连：connector.connect → calls.invokeOnce → 目标 runtime
            var direct = await host.InvokeAsync(caller.SpawnId, "dep-direct", null);
            Assert.True(direct.Ok, $"dep-direct 失败: {direct.Error?.Message}");
            Assert.Equal(2, direct.Result.GetProperty("dep").GetProperty("y").GetInt32());
        }
        finally
        {
            await callerRuntime.DisposeAsync();
            await depRuntime.DisposeAsync();
            ClearEnvironment();
        }
    }

    /// <summary>
    /// window.webcontents_send_parent：命令内 ScopedUI 窗口自动带当前 invocationId；
    /// brick 级（无 scope）窗口必须显式 payload.requestId，否则 PARENT_INVOCATION_REQUIRED。
    /// </summary>
    // @capability window.webcontents_send_parent
    [Fact]
    public async Task WindowWebContentsSendParent()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return;
        }

        var spawn = await host.SpawnAsync("drive-window");
        var runtime = new BricklyRuntime();
        runtime.OnCommand("send-scoped", async (ctx, _) =>
        {
            var window = await ctx.UI().CreateBrowserWindowAsync("https://example.com");
            await window.WebContents().SendAsync(
                "channel-a", new Dictionary<string, object?> { ["hello"] = 1 });
            return "sent";
        });
        runtime.OnCommand("send-unscoped", async (ctx, _) =>
        {
            // brick 级 UI 创建的窗口不绑 command parent
            var window = await runtime.UI.CreateBrowserWindowAsync("https://example.com");
            try
            {
                await window.WebContents().SendAsync("channel-b", (object?)null);
                return "NO_ERROR";
            }
            catch (BppException error)
            {
                return error.Code;
            }
        });
        runtime.OnCommand("send-explicit", async (ctx, _) =>
        {
            var window = await runtime.UI.CreateBrowserWindowAsync("https://example.com");
            await window.WebContents().SendAsync(
                "channel-c",
                new Dictionary<string, object?> { ["requestId"] = "req-9", ["v"] = 3 });
            return "sent";
        });

        ApplyEnvironment(host, spawn);
        try
        {
            await runtime.StartAsync();
            await host.SetUiResponseAsync("createBrowserWindow", new
            {
                windowKey = "w1",
                windowId = 42,
                webContentsId = 7,
                url = "https://example.com",
            });
            await host.SetUiResponseAsync("callWindow", new { ok = true });

            // 命令内 send：parentRequestId 自动等于本次驱动调用的 invocationId
            var scoped = await host.InvokeAsync(spawn.SpawnId, "send-scoped", null);
            Assert.True(scoped.Ok, $"send-scoped 失败: {scoped.Error?.Message}");
            var scopedCall = Assert.Single(
                await host.UiCallsAsync(),
                call => call.Method == "callWindow"
                    && call.Args.GetArrayLength() >= 6
                    && call.Args[3].GetString() == "webContents.send");
            Assert.Equal("channel-a", scopedCall.Args[4][0].GetString());
            Assert.Equal(scoped.InvocationId, scopedCall.Args[5].GetString());

            // 无 scope 窗口 + 无 requestId：拒绝
            var unscoped = await host.InvokeAsync(spawn.SpawnId, "send-unscoped", null);
            Assert.True(unscoped.Ok, $"send-unscoped 失败: {unscoped.Error?.Message}");
            Assert.Equal("PARENT_INVOCATION_REQUIRED", unscoped.Result.GetString());

            // 无 scope 窗口 + 显式 payload.requestId：放行且透传
            var explicit_ = await host.InvokeAsync(spawn.SpawnId, "send-explicit", null);
            Assert.True(explicit_.Ok, $"send-explicit 失败: {explicit_.Error?.Message}");
            var explicitCall = Assert.Single(
                await host.UiCallsAsync(),
                call => call.Method == "callWindow"
                    && call.Args.GetArrayLength() >= 6
                    && call.Args[4].GetArrayLength() > 0
                    && call.Args[4][0].GetString() == "channel-c");
            Assert.Equal("req-9", explicitCall.Args[5].GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            ClearEnvironment();
        }
    }

    /// <summary>把 /spawn 凭据注入为 Runtime 启动环境（RuntimeEnv.Take 一次性消费）。</summary>
    private static void ApplyEnvironment(TestHostProcess host, TestHostProcess.SpawnInfo spawn)
    {
        Environment.SetEnvironmentVariable("BRICKLY_HOST_ENDPOINT", host.DataEndpoint);
        Environment.SetEnvironmentVariable("BRICKLY_BOOTSTRAP_TOKEN", spawn.BootstrapToken);
        Environment.SetEnvironmentVariable("BRICKLY_RUNTIME_TO_HOST_TOKEN", spawn.RuntimeToHostToken);
        Environment.SetEnvironmentVariable("BRICKLY_HOST_TO_RUNTIME_TOKEN", spawn.HostToRuntimeToken);
    }

    private static void ClearEnvironment()
    {
        foreach (var name in new[]
        {
            "BRICKLY_HOST_ENDPOINT",
            "BRICKLY_BOOTSTRAP_TOKEN",
            "BRICKLY_RUNTIME_TO_HOST_TOKEN",
            "BRICKLY_HOST_TO_RUNTIME_TOKEN",
            "BRICKLY_DEPENDENCY_BINDINGS",
        })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
