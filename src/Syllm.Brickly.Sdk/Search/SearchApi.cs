namespace Syllm.Brickly.Sdk;

/// <summary>
/// 快速搜索消费通道（对应 PlatformService `search.*`）。
/// manifest 须声明 `quickSearch.consumer`，否则宿主拒绝该 Brick 的搜索调用。
/// 消费方拿到的结果不含路由载荷，激活凭 resultId 回宿主路由表执行。
/// </summary>
public sealed class SearchApi
{
    private readonly BricklyRuntime _runtime;

    internal SearchApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>发起一次搜索（blocking：等全部命中 Provider 完成后返回快照）。</summary>
    public Task<SearchQueryResponse> QueryAsync(
        SearchQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _runtime.PlatformCallAsync<SearchQueryResponse>("search.query", request, cancellationToken);
    }

    /// <summary>激活一条结果的默认路由。</summary>
    public Task<SearchActivationResult> ActivateAsync(
        string resultId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultId);
        return _runtime.PlatformCallAsync<SearchActivationResult>(
            "search.activate",
            new Dictionary<string, object?> { ["resultId"] = resultId },
            cancellationToken);
    }

    /// <summary>执行一条结果的菜单动作。</summary>
    public Task<SearchActivationResult> RunActionAsync(
        string resultId,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultId);
        ArgumentException.ThrowIfNullOrEmpty(actionId);
        return _runtime.PlatformCallAsync<SearchActivationResult>(
            "search.runAction",
            new Dictionary<string, object?> { ["resultId"] = resultId, ["actionId"] = actionId },
            cancellationToken);
    }
}
