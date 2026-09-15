using System.Text.Json;
using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class WindowTests
{
    [Fact]
    public void WhitelistMatchesWindowProtocolSchema()
    {
        var path = FindRepoFile("specs/window-protocol.schema.json");
        if (path is null)
        {
            return; // 独立仓库无 specs 目录：跳过（与 Go SDK 同策略）
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var definitions = root.TryGetProperty("definitions", out var defs)
            ? defs
            : root.GetProperty("$defs");
        var schemaMethods = definitions
            .GetProperty("BrickWindowMethod")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var sdkMethods = BrickWindowMethods.All
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(schemaMethods, sdkMethods);
    }

    [Fact]
    public async Task ClosePendingKeepsHandleUsable()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            await host.SetUiResponseAsync("closeWindow", new Dictionary<string, object?>
            {
                ["status"] = "pending",
            });
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");

            var result = await handle.CloseAsync();
            Assert.Equal(WindowCloseStatuses.Pending, result.Status);
            Assert.False(handle.IsClosed);

            await host.SetUiResponseAsync("closeWindow", new Dictionary<string, object?>
            {
                ["status"] = "closed",
            });
            var second = await handle.CloseAsync();
            Assert.Equal(WindowCloseStatuses.Closed, second.Status);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CloseTerminalClearsHandle()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            await host.SetUiResponseAsync("closeWindow", new Dictionary<string, object?>
            {
                ["status"] = "closed",
            });
            await host.SetUiResponseAsync("callWindow", true);
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            await handle.CloseAsync();
            Assert.True(handle.IsClosed);

            var error = await Assert.ThrowsAsync<BppException>(() => handle.CallAsync("getTitle"));
            Assert.Equal(BppErrorCodes.InvalidInput, error.Code);

            var destroyed = await handle.IsDestroyedAsync();
            Assert.True(destroyed);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task WindowCallFallback()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            await host.SetUiResponseAsync("callWindow", "Hello Title");
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var title = await handle.CallAsync<string>("getTitle");
            Assert.Equal("Hello Title", title);

            var raw = await handle.CallAsync("getTitle");
            Assert.Equal("Hello Title", raw.GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task WebContentsSendOutsideCommandRequiresParent()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var error = await Assert.ThrowsAsync<BppException>(() => handle.WebContents().SendAsync("channel"));
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ScopedWindowUsesCallBindingAndSessionWindowUsesSessionBinding()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("open", async (ctx, _) =>
            {
                var window = await ctx.UI().CreateBrowserWindowAsync("about:blank");
                return window.ID;
            }));
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            using var client = await TestHarness.CreateRuntimeClientAsync(host);
            await client.InvokeAsync("open", null);

            // createBrowserWindow(caller, instanceId, url, options, invocationId)
            // → args[3] 为 windowOptionsFromWire 透传的 options（含 binding.kind）
            var callScoped = await host.WaitCallAsync(call =>
                call.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("method", out var m) &&
                m.GetString() == "ui.window.create" &&
                BindingKindOf(call.Request) == "call");
            Assert.NotNull(callScoped);

            await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var sessionScoped = await host.WaitCallAsync(call =>
                call.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("method", out var m) &&
                m.GetString() == "ui.window.create" &&
                BindingKindOf(call.Request) == "session");
            Assert.NotNull(sessionScoped);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task WindowClosedEventDedupBounded()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var count = 0;
            using var subscription = handle.On("closed", _ => Interlocked.Increment(ref count));

            // 真宿主要求订阅流已建立再推事件（FakeHost 的内存字典不需要等）
            await host.WaitCallAsync(call =>
                call.Path.EndsWith("EventService/Subscribe", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("topic", out var t) &&
                t.GetString() == "window.closed");

            var payload = new Dictionary<string, object?>
            {
                ["eventId"] = "event-1",
                ["windowId"] = 1L,
            };
            await host.PushEventAsync(host.LastSpawn!.SpawnId, "window.closed", payload);
            await TestHarness.WaitUntilAsync(() => Volatile.Read(ref count) == 1);
            await host.PushEventAsync(host.LastSpawn!.SpawnId, "window.closed", payload);
            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref count));
            Assert.True(handle.IsClosed);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeEndDisposesAllWindowHandles()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            Assert.False(handle.IsClosed);
            await runtime.DisposeAsync();
            Assert.True(handle.IsClosed);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task ExposeRoundTrip()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            await host.SetUiResponseAsync("replyToChild", null);
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            await handle.ExposeAsync("greet", (payload, _) => Task.FromResult<object?>("hi " + payload));

            await host.WaitCallAsync(call =>
                call.Path.EndsWith("EventService/Subscribe", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("topic", out var t) &&
                t.GetString() == "window.request");

            await host.PushEventAsync(host.LastSpawn!.SpawnId, "window.request", new Dictionary<string, object?>
            {
                ["name"] = "greet",
                ["requestId"] = "req-1",
                ["windowId"] = 1L,
                ["payload"] = "bob",
            });

            var reply = await host.WaitCallAsync(call =>
                call.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("method", out var m) &&
                m.GetString() == "ui.window.reply");
            var payloadMap = Assert.IsType<Dictionary<string, object?>>(
                WireValue.InputOf(reply.Request));
            Assert.Equal(true, payloadMap["ok"]);
            Assert.Equal("hi bob", payloadMap["result"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ExposeRejectsReservedName()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var error = await Assert.ThrowsAsync<BppException>(
                () => handle.ExposeAsync("brickly:internal", (_, _) => Task.FromResult<object?>(null)));
            Assert.Equal("RESERVED_NAME", error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task GeometryWrappersEncodeArguments()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetUiResponseAsync("createBrowserWindow", WindowCreateResult());
            // setSize 走 CallVoidAsync 忽略返回值，单一罐头即可覆盖两种 method
            await host.SetUiResponseAsync("callWindow", new Dictionary<string, object?>
            {
                ["x"] = 1L,
                ["y"] = 2L,
                ["width"] = 640L,
                ["height"] = 480L,
            });
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var bounds = await handle.GetBoundsAsync();
            Assert.Equal(640, bounds.Width);
            Assert.Equal(480, bounds.Height);
            await handle.SetSizeAsync(800, 600);

            var call = await host.WaitCallAsync(item =>
                item.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal) &&
                item.Request.TryGetProperty("method", out var m) &&
                m.GetString() == "ui.window.call" &&
                WireValue.InputOf(item.Request) is Dictionary<string, object?> input &&
                input.TryGetValue("method", out var name) &&
                Equals(name, "setSize"));
            var payloadMap = Assert.IsType<Dictionary<string, object?>>(
                WireValue.InputOf(call.Request));
            var args = Assert.IsType<List<object?>>(payloadMap["args"]);
            Assert.Equal(800L, Convert.ToInt64(args[0]));
            Assert.Equal(600L, Convert.ToInt64(args[1]));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    /// <summary>从录制的 ui.window.create 请求体解出 options.binding.kind。</summary>
    private static string? BindingKindOf(JsonElement request)
    {
        if (WireValue.InputOf(request) is not Dictionary<string, object?> input ||
            !input.TryGetValue("options", out var options) ||
            options is not Dictionary<string, object?> optionMap ||
            !optionMap.TryGetValue("binding", out var binding) ||
            binding is not Dictionary<string, object?> bindingMap)
        {
            return null;
        }
        return bindingMap.TryGetValue("kind", out var kind) ? kind as string : null;
    }

    private static Dictionary<string, object?> WindowCreateResult() => new()
    {
        ["windowKey"] = "wk-1",
        ["windowId"] = 1L,
        ["webContentsId"] = 2L,
        ["url"] = "about:blank",
    };

    private static string? FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
