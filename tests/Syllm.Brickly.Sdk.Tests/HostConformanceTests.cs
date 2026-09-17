using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

/// <summary>
/// 真宿主 storage conformance：真 HostGrpcServer（brickly-test-host 工件）
/// × 真 internal 客户端。覆盖真实现读写、asDocument 文档形状、
/// 缺失文档错误码、SetNextError 短路注入无副作用、控制面录制。
/// 工件未装或无 node 时跳过（与 Python/Go conformance 同策略）。
/// </summary>
public class HostConformanceTests
{
    private const string KvSetPath = "/brickly.runtime.v1.BrickStorageService/KvSet";
    private const string KvGetPath = "/brickly.runtime.v1.BrickStorageService/KvGet";

    [Fact]
    public async Task RealHostStorageConformance()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return; // 未安装 @syllm/brickly-test-host 或无 node：跳过
        }
        using var client = new HostBrickStorageClient(host.DataEndpoint, host.RuntimeToHostToken);
        var ct = CancellationToken.None;

        // KV 经真实现读写
        await client.KvSetAsync("user", "name", "brickly", ct);
        var (value, found) = await client.KvGetAsync("user", "name", ct);
        Assert.True(found);
        Assert.Equal("brickly", value);
        Assert.Equal(["name"], await client.KvListAsync("user", "", ct));

        // 真宿主 asDocument 语义：id/revision/updatedAt 合并进 data 返回
        var created = await client.CreateDocAsync(
            "user", "notes", new Dictionary<string, object?> { ["title"] = "hello" }, ct);
        Assert.Equal("hello", created["title"]);
        var id = Assert.IsType<string>(created["id"]);
        Assert.StartsWith("r_", Assert.IsType<string>(created["revision"]));
        Assert.True(Convert.ToDouble(created["updatedAt"]) > 0);

        var loaded = await client.GetDocAsync("user", "notes", id, ct);
        Assert.NotNull(loaded);
        Assert.Equal("hello", loaded["title"]);
        Assert.Equal(id, loaded["id"]);

        // 缺失文档 update → STORAGE_NOT_FOUND（假宿主漂移高发点）
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(
            () => client.UpdateDocAsync(
                "user", "notes", "missing-doc",
                new Dictionary<string, object?> { ["title"] = "x" }, ct));
        Assert.Equal("STORAGE_NOT_FOUND", BrickErrorStatus.TryReadBrickError(error)?.Code);

        // 控制面录制
        var calls = await host.CallsAsync();
        Assert.Contains(calls, c => c.Path.EndsWith(KvSetPath, StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Path.EndsWith(KvGetPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealHostFaultInjectionIsSideEffectFree()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return;
        }
        using var client = new HostBrickStorageClient(host.DataEndpoint, host.RuntimeToHostToken);
        var ct = CancellationToken.None;

        await host.SetFaultAsync(KvSetPath, "LIMIT_EXCEEDED", count: 1);
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(
            () => client.KvSetAsync("user", "blocked", "x", ct));
        Assert.Equal("LIMIT_EXCEEDED", BrickErrorStatus.TryReadBrickError(error)?.Code);
        Assert.False(await client.KvHasAsync("user", "blocked", ct), "被注入的写入不应产生副作用");

        // 规则只拦一次，随后放行
        await client.KvSetAsync("user", "after-fault", "ok", ct);
        var (value, found) = await client.KvGetAsync("user", "after-fault", ct);
        Assert.True(found);
        Assert.Equal("ok", value);

        // reset 清空规则与录制
        await host.SetFaultAsync(KvGetPath, "NOT_FOUND", count: 5);
        await host.ResetAsync();
        Assert.Empty(await host.CallsAsync());
        var (after, stillFound) = await client.KvGetAsync("user", "after-fault", ct);
        Assert.True(stillFound);
        Assert.Equal("ok", after);
    }

    /// <summary>
    /// 真宿主 search.* consumer conformance：覆盖 search.query/activate/runAction 的
    /// wire 形状（method 名 + input 透传 + 回包解析）、/search-calls 录制
    /// （method、原始入参、callerBrickId）、未注册罐头时 INTERNAL 回传、reset 清空。
    /// SDK 门面层（Platform.Search.*）的方法名映射由 SearchTests 锁定；
    /// 本用例与 storage conformance 同层——只验客户端与真宿主之间的协议契约。
    /// </summary>
    [Fact]
    public async Task RealHostSearchConsumerConformance()
    {
        using var host = TestHostProcess.TryStart();
        if (host is null)
        {
            return;
        }
        using var client = new HostPlatformClient(host.DataEndpoint, host.RuntimeToHostToken);
        var ct = CancellationToken.None;

        // 三个方法各注册一份可辨识的罐头响应
        await host.SetSearchResponseAsync("query", new Dictionary<string, object?>
        {
            ["results"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "r1", ["title"] = "命中一", ["subtitle"] = "sub",
                    ["icon"] = "icons/a.png", ["score"] = 0.9,
                },
            },
            ["providers"] = new List<object?>
            {
                new Dictionary<string, object?> { ["providerId"] = "p1", ["status"] = "complete" },
            },
            ["sequence"] = 7,
        });
        await host.SetSearchResponseAsync(
            "activate", new Dictionary<string, object?> { ["handled"] = true, ["commandId"] = "open" });
        await host.SetSearchResponseAsync(
            "runAction", new Dictionary<string, object?> { ["handled"] = false });

        // search.query：入参透传 + 回包形状
        var queryResult = await client.PlatformCallAsync(
            "search.query",
            new Dictionary<string, object?> { ["query"] = "hello", ["limit"] = 5, ["sequence"] = 3 },
            null, ct);
        var queryMap = Assert.IsType<Dictionary<string, object?>>(queryResult);
        var results = Assert.IsType<List<object?>>(queryMap["results"]);
        var item = Assert.IsType<Dictionary<string, object?>>(results[0]);
        Assert.Equal("r1", item["id"]);
        Assert.Equal("命中一", item["title"]);
        var providers = Assert.IsType<List<object?>>(queryMap["providers"]);
        Assert.Single(providers);

        var activateResult = Assert.IsType<Dictionary<string, object?>>(
            await client.PlatformCallAsync(
                "search.activate", new Dictionary<string, object?> { ["resultId"] = "r1" }, null, ct));
        Assert.Equal(true, activateResult["handled"]);
        Assert.Equal("open", activateResult["commandId"]);

        var actionResult = Assert.IsType<Dictionary<string, object?>>(
            await client.PlatformCallAsync(
                "search.runAction",
                new Dictionary<string, object?> { ["resultId"] = "r1", ["actionId"] = "copy" },
                null, ct));
        Assert.Equal(false, actionResult["handled"]);

        // /search-calls：method 序、input 透传、caller = 默认 spawn 的砖 id
        var calls = await host.SearchCallsAsync();
        Assert.Equal(3, calls.Count);
        Assert.Equal("query", calls[0].Method);
        Assert.Equal("activate", calls[1].Method);
        Assert.Equal("runAction", calls[2].Method);
        Assert.Equal("hello", calls[0].Input.GetProperty("query").GetString());
        Assert.Equal(5, calls[0].Input.GetProperty("limit").GetInt32());
        Assert.Equal("copy", calls[2].Input.GetProperty("actionId").GetString());
        Assert.All(calls, c => Assert.Equal("com.brickly.testhost", c.CallerBrickId));

        // 错误传播：reset 清罐头后再调 → handler 抛错 → INTERNAL 回传
        await host.ResetAsync();
        Assert.Empty(await host.SearchCallsAsync());
        var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(
            () => client.PlatformCallAsync(
                "search.query", new Dictionary<string, object?> { ["query"] = "x" }, null, ct));
        Assert.Equal(global::Grpc.Core.StatusCode.Internal, error.StatusCode);
    }
}
