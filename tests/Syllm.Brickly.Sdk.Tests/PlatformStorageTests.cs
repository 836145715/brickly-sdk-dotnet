using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class PlatformStorageTests
{
    [Fact]
    public async Task EventsOnPublishRoundTrip()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var received = System.Threading.Channels.Channel.CreateUnbounded<Dictionary<string, object?>>();
            using var subscription = runtime.Events.On("my-brick:tick", (payload, _) =>
            {
                if (payload is Dictionary<string, object?> map)
                {
                    received.Writer.TryWrite(map);
                }
            });
            // 订阅是 server-streaming 调用，录制到 Subscribe 才算投递就绪
            await host.WaitCallAsync(call =>
                call.Path.EndsWith("EventService/Subscribe", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("topic", out var t) &&
                t.GetString() == "my-brick:tick");

            // 真宿主语义：publish 经 HostEventBus 环回投递给本 runtime 的订阅流
            await runtime.Events.PublishAsync("my-brick:tick", new { n = 1 });
            await host.WaitCallAsync(call =>
                call.Path.EndsWith("EventService/Publish", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("topic", out var t) &&
                t.GetString() == "my-brick:tick");

            await host.PushEventAsync(
                host.LastSpawn!.SpawnId,
                "my-brick:tick",
                new Dictionary<string, object?> { ["n"] = 2L });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var echoed = await received.Reader.ReadAsync(timeout.Token);
            Assert.Equal(1L, Convert.ToInt64(echoed["n"]));
            var pushed = await received.Reader.ReadAsync(timeout.Token);
            Assert.Equal(2L, Convert.ToInt64(pushed["n"]));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task EventNameValidation()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var onError = Assert.Throws<BppException>(() => runtime.Events.On("bad", (_, _) => { }));
            Assert.Equal(BppErrorCodes.InvalidInput, onError.Code);

            var publishError = await Assert.ThrowsAsync<BppException>(
                () => runtime.Events.PublishAsync("bad", null));
            Assert.Equal(BppErrorCodes.InvalidInput, publishError.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PlatformCallRoutesClipboardScreenInputSystem()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await host.SetPlatformResponseAsync("clipboard.readContent", new Dictionary<string, object?>
            {
                ["kind"] = "text",
                ["text"] = "clip",
            });
            await host.SetPlatformResponseAsync("screen.getPrimaryDisplay", new Dictionary<string, object?>
            {
                ["id"] = 1L,
                ["width"] = 1920L,
            });
            await host.SetPlatformResponseAsync("system.getPath", "C:/data");
            await host.SetPlatformResponseAsync("system.isWindows", true);
            // 真宿主语义：未注册方法报"宿主未接入"，不再静默返回空值
            await host.SetPlatformResponseAsync("input.mouseMove", null);
            await host.SetPlatformResponseAsync("screenshot.selectRegion", null);

            var clipboard = await runtime.Platform.Clipboard.ReadContentAsync();
            Assert.Equal("clip", clipboard["text"]);

            var display = await runtime.Platform.Screen.GetPrimaryDisplayAsync();
            Assert.Equal(1920L, display["width"]);

            var path = await runtime.System.GetPathAsync(SystemPathName.UserData);
            Assert.Equal("C:/data", path);

            Assert.True(await runtime.System.IsWindowsAsync());

            await runtime.Platform.Input.MouseMoveAsync(new ScreenPoint { X = 1, Y = 2 });
            await runtime.Platform.Screenshot.SelectRegionAsync();

            var calls = await host.CallsAsync();
            var platformCalls = calls
                .Where(call => call.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal))
                .ToList();
            Assert.Contains(platformCalls, call => MethodIs(call, "clipboard.readContent"));
            Assert.Contains(platformCalls, call => MethodIs(call, "screen.getPrimaryDisplay"));
            var getPath = platformCalls.First(call => MethodIs(call, "system.getPath"));
            Assert.Equal("userData", WireValue.InputOf(getPath.Request));
            Assert.Contains(platformCalls, call => MethodIs(call, "input.mouseMove"));
            Assert.Contains(platformCalls, call => MethodIs(call, "screenshot.selectRegion"));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StorageKvCrudAndIsolation()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            await runtime.Storage.KV.SetAsync("name", "brickly");
            Assert.Equal("brickly", await runtime.Storage.KV.GetAsync("name"));
            Assert.True(await runtime.Storage.KV.HasAsync("name"));
            Assert.Contains("name", await runtime.Storage.KV.ListAsync());

            await runtime.Storage.Secrets.SetAsync("name", "secret");
            Assert.Equal("brickly", await runtime.Storage.KV.GetAsync("name"));
            Assert.Equal("secret", await runtime.Storage.Secrets.GetAsync("name"));

            Assert.True(await runtime.Storage.KV.DeleteAsync("name"));
            Assert.Null(await runtime.Storage.KV.GetAsync("name"));

            var status = await runtime.Storage.StatusAsync();
            // 真实内存宿主未接账号体系：signedIn=false（FakeHost 曾演成 true，属语义漂移）
            Assert.False((bool)status["signedIn"]!);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StorageDocumentsRoundTrip()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var collection = runtime.Storage.Collection("notes");
            var created = await collection.CreateAsync(new Dictionary<string, object?>
            {
                ["title"] = "hello",
            });
            Assert.Equal("hello", created["title"]);
            // 生产 asDocument 语义：id / revision / updatedAt 合并进 data 返回
            Assert.True(created["id"] is string createdId && createdId.Length > 0);
            Assert.True(created["revision"] is string revision && revision.Length > 0);

            var docs = await collection.ListAsync();
            Assert.NotEmpty(docs);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StorageUpdateMissingDocumentIsStorageNotFound()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var collection = runtime.Storage.Collection("notes");
            var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(
                () => collection.UpdateAsync("missing-doc", new Dictionary<string, object?> { ["title"] = "x" }));
            Assert.Equal(global::Grpc.Core.StatusCode.NotFound, error.StatusCode);
            // 生产同时携带 BrickError details；SDK 用户可从中还原 STORAGE_NOT_FOUND
            var brickError = BrickErrorStatus.TryReadBrickError(error);
            Assert.NotNull(brickError);
            Assert.Equal("STORAGE_NOT_FOUND", brickError!.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StoragePutUpsertsByDocumentId()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var collection = runtime.Storage.Collection("notes");
            var first = await collection.PutAsync(new Dictionary<string, object?>
            {
                ["id"] = "custom-1",
                ["title"] = "v1",
            });
            Assert.Equal("custom-1", first["id"]);
            Assert.Equal("v1", first["title"]);

            var second = await collection.PutAsync(new Dictionary<string, object?>
            {
                ["id"] = "custom-1",
                ["title"] = "v2",
            });
            Assert.Equal("custom-1", second["id"]);
            Assert.Equal("v2", second["title"]);

            var docs = await collection.ListAsync();
            Assert.Single(docs);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task StorageWatchEmitsChanges()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var received = new TaskCompletionSource<Dictionary<string, object?>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var collection = runtime.Storage.Collection("notes");
            var subscription = await collection.WatchAsync(change =>
            {
                received.TrySetResult(change);
            });
            try
            {
                // WatchDocs 建立后再真写，宿主推送真实变更事件（不再注入内存桩）
                await host.WaitCallAsync(call =>
                    call.Path.EndsWith("BrickStorageService/WatchDocs", StringComparison.Ordinal));
                var created = await collection.CreateAsync(new Dictionary<string, object?>
                {
                    ["title"] = "changed",
                });
                var createdId = Assert.IsType<string>(created["id"]);

                var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("put", change["type"]);
                Assert.Equal(createdId, change["id"]);
            }
            finally
            {
                subscription.Dispose();
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ProfileConfigSnapshotIsInjected()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(
            profileConfig: """{"host":"db","port":5432}""");
        try
        {
            Assert.Equal("db", runtime.Config["host"]);
            Assert.Equal(5432L, Convert.ToInt64(runtime.Config["port"]));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ConfigIsVisibleInsideCommand()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(
            configure: r => r.OnCommand("show-config", (ctx, _) =>
                Task.FromResult<object?>(ctx.Config.TryGetValue("host", out var hostName) ? hostName : null)),
            profileConfig: """{"host":"db"}""");
        try
        {
            using var client = await TestHarness.CreateRuntimeClientAsync(host);
            var result = await client.InvokeAsync("show-config", null);
            Assert.Equal("db", BrickValueCodec.ToClr(result.Result));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    private static bool MethodIs(TestHostProcess.RecordedCall call, string method)
    {
        return call.Request.TryGetProperty("method", out var value) &&
            value.GetString() == method;
    }
}
