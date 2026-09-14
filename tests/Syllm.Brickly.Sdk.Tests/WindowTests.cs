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
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            host.PlatformCallHandlers["ui.window.requestClose"] = _ => new Dictionary<string, object?>
            {
                ["status"] = "pending",
            };
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");

            var result = await handle.CloseAsync();
            Assert.Equal(WindowCloseStatuses.Pending, result.Status);
            Assert.False(handle.IsClosed);

            host.PlatformCallHandlers["ui.window.requestClose"] = _ => new Dictionary<string, object?>
            {
                ["status"] = "closed",
            };
            var second = await handle.CloseAsync();
            Assert.Equal(WindowCloseStatuses.Closed, second.Status);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task CloseTerminalClearsHandle()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            host.PlatformCallHandlers["ui.window.requestClose"] = _ => new Dictionary<string, object?>
            {
                ["status"] = "closed",
            };
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
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WindowCallFallback()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            host.PlatformCallHandlers["ui.window.call"] = _ => "Hello Title";
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var title = await handle.CallAsync<string>("getTitle");
            Assert.Equal("Hello Title", title);

            var raw = await handle.CallAsync("getTitle");
            Assert.Equal("Hello Title", raw.GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebContentsSendOutsideCommandRequiresParent()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var error = await Assert.ThrowsAsync<BppException>(() => handle.WebContents().SendAsync("channel"));
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScopedWindowUsesCallBindingAndSessionWindowUsesSessionBinding()
    {
        Dictionary<string, object?>? sessionOptions = null;
        Dictionary<string, object?>? callOptions = null;
        var host = await FakeHost.StartAsync();
        host.PlatformCallHandlers["ui.window.create"] = input =>
        {
            var payload = (Dictionary<string, object?>)BrickValueCodec.ToClr(input)!;
            var options = (Dictionary<string, object?>)payload["options"]!;
            var binding = (Dictionary<string, object?>)options["binding"]!;
            if (EqualityComparer<object?>.Default.Equals(binding["kind"], "call"))
            {
                callOptions = options;
            }
            else
            {
                sessionOptions = options;
            }
            return WindowCreateResult();
        };
        host.ApplyEnvironment();
        var runtime = new BricklyRuntime().OnCommand("open", async (ctx, _) =>
        {
            var window = await ctx.UI().CreateBrowserWindowAsync("about:blank");
            return window.ID;
        });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("open", null);
            Assert.NotNull(callOptions);
            Assert.Equal("call", ((Dictionary<string, object?>)callOptions!["binding"]!)["kind"]);

            await runtime.UI.CreateBrowserWindowAsync("about:blank");
            Assert.NotNull(sessionOptions);
            Assert.Equal("session", ((Dictionary<string, object?>)sessionOptions!["binding"]!)["kind"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WindowClosedEventDedupBounded()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var count = 0;
            using var subscription = handle.On("closed", _ => Interlocked.Increment(ref count));

            var payload = BrickValueCodec.FromClr(new Dictionary<string, object?>
            {
                ["eventId"] = "event-1",
                ["windowId"] = 1L,
            });
            host.PushDomainEvent("window.closed", payload);
            await TestHarness.WaitUntilAsync(() => Volatile.Read(ref count) == 1);
            host.PushDomainEvent("window.closed", payload);
            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref count));
            Assert.True(handle.IsClosed);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeEndDisposesAllWindowHandles()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            Assert.False(handle.IsClosed);
            await runtime.DisposeAsync();
            Assert.True(handle.IsClosed);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExposeRoundTrip()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            await handle.ExposeAsync("greet", (payload, _) => Task.FromResult<object?>("hi " + payload));

            host.PushDomainEvent("window.request", BrickValueCodec.FromClr(new Dictionary<string, object?>
            {
                ["name"] = "greet",
                ["requestId"] = "req-1",
                ["windowId"] = 1L,
                ["payload"] = "bob",
            }));

            await TestHarness.WaitUntilAsync(() => host.PlatformCalls.Any(call => call.Method == "ui.window.reply"));
            var reply = host.PlatformCalls.Last(call => call.Method == "ui.window.reply");
            var payloadMap = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(reply.Input));
            Assert.True((bool)payloadMap["ok"]!);
            Assert.Equal("hi bob", payloadMap["result"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExposeRejectsReservedName()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var error = await Assert.ThrowsAsync<BppException>(
                () => handle.ExposeAsync("brickly:internal", (_, _) => Task.FromResult<object?>(null)));
            Assert.Equal("RESERVED_NAME", error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task GeometryWrappersEncodeArguments()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["ui.window.create"] = _ => WindowCreateResult();
            host.PlatformCallHandlers["ui.window.call"] = input =>
            {
                var payload = (Dictionary<string, object?>)BrickValueCodec.ToClr(input)!;
                if (Equals(payload["method"], "getBounds"))
                {
                    return new Dictionary<string, object?>
                    {
                        ["x"] = 1L,
                        ["y"] = 2L,
                        ["width"] = 640L,
                        ["height"] = 480L,
                    };
                }
                return true;
            };
            var handle = await runtime.UI.CreateBrowserWindowAsync("about:blank");
            var bounds = await handle.GetBoundsAsync();
            Assert.Equal(640, bounds.Width);
            Assert.Equal(480, bounds.Height);
            await handle.SetSizeAsync(800, 600);

            var call = host.PlatformCalls.Last(call => call.Method == "ui.window.call");
            var payloadMap = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(call.Input));
            Assert.Equal("setSize", payloadMap["method"]);
            var args = Assert.IsType<List<object?>>(payloadMap["args"]);
            Assert.Equal(800L, Convert.ToInt64(args[0]));
            Assert.Equal(600L, Convert.ToInt64(args[1]));
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    private static Dictionary<string, object?> WindowCreateResult() => new()
    {
        ["windowKey"] = "wk-1",
        ["windowId"] = 1L,
        ["webContentsId"] = 2L,
        ["url"] = "about:blank",
    };

    private static string FindRepoFile(string relativePath)
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
        throw new FileNotFoundException(relativePath);
    }
}
