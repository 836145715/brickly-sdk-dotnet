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
            var received = new TaskCompletionSource<Dictionary<string, object?>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = runtime.Events.On("my-brick:tick", (payload, _) =>
            {
                if (payload is Dictionary<string, object?> map)
                {
                    received.TrySetResult(map);
                }
            });
            await TestHarness.WaitUntilAsync(() => host.SubscribedTopics.Contains("my-brick:tick"));

            await runtime.Events.PublishAsync("my-brick:tick", new { n = 1 });
            await TestHarness.WaitUntilAsync(() => host.PublishedEvents.Any(e => e.Topic == "my-brick:tick"));

            host.PushDomainEvent("my-brick:tick", BrickValueCodec.FromClr(new { n = 2 }));
            var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2L, value["n"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
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
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlatformCallRoutesClipboardScreenInputSystem()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            host.PlatformCallHandlers["clipboard.readContent"] = _ => new Dictionary<string, object?>
            {
                ["kind"] = "text",
                ["text"] = "clip",
            };
            host.PlatformCallHandlers["screen.getPrimaryDisplay"] = _ => new Dictionary<string, object?>
            {
                ["id"] = 1L,
                ["width"] = 1920L,
            };
            host.PlatformCallHandlers["system.getPath"] = _ => "C:/data";
            host.PlatformCallHandlers["system.isWindows"] = _ => true;

            var clipboard = await runtime.Platform.Clipboard.ReadContentAsync();
            Assert.Equal("clip", clipboard["text"]);

            var display = await runtime.Platform.Screen.GetPrimaryDisplayAsync();
            Assert.Equal(1920L, display["width"]);

            var path = await runtime.System.GetPathAsync(SystemPathName.UserData);
            Assert.Equal("C:/data", path);

            Assert.True(await runtime.System.IsWindowsAsync());

            await runtime.Platform.Input.MouseMoveAsync(new ScreenPoint { X = 1, Y = 2 });
            await runtime.Platform.Screenshot.SelectRegionAsync();

            Assert.Contains(host.PlatformCalls, call => call.Method == "clipboard.readContent");
            Assert.Contains(host.PlatformCalls, call => call.Method == "screen.getPrimaryDisplay");
            var getPath = host.PlatformCalls.First(call => call.Method == "system.getPath");
            Assert.Equal("userData", BrickValueCodec.ToClr(getPath.Input));
            Assert.Contains(host.PlatformCalls, call => call.Method == "input.mouseMove");
            Assert.Contains(host.PlatformCalls, call => call.Method == "screenshot.selectRegion");
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
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
            Assert.True((bool)status["signedIn"]!);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
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
            await host.DisposeAsync();
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
            await host.DisposeAsync();
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
            await host.DisposeAsync();
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
            var subscription = await runtime.Storage.Collection("notes").WatchAsync(change =>
            {
                received.TrySetResult(change);
            });
            try
            {
                await TestHarness.WaitUntilAsync(() => host.WatchSubscribers > 0);
                host.PushWatchEvent(new BrickStorageChangeEvent
                {
                    Type = "put",
                    Id = "doc-1",
                    Doc = BrickValueCodec.FromClr(new Dictionary<string, object?> { ["title"] = "changed" }),
                });
                var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("put", change["type"]);
                Assert.Equal("doc-1", change["id"]);
            }
            finally
            {
                subscription.Dispose();
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
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
            await host.DisposeAsync();
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
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("show-config", null);
            Assert.Equal("db", BrickValueCodec.ToClr(result.Result));
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }
}
