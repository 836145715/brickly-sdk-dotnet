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
}
