using System.Text.Json.Serialization;

namespace Syllm.Brickly.Sdk;

// —— Provider 侧类型（manifest commands[] 带 provider 标记的端点）——

/// <summary>消费方 BrickRef；内置搜索砖固定 com.brickly.quick-search。</summary>
public sealed class SearchCaller
{
    [JsonPropertyName("brickId")]
    public required string BrickId { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>
/// 结果自带激活路由：指向同砖内未标 provider 的普通命令。
/// <see cref="Input"/> 只存宿主路由表，不进入消费方。
/// </summary>
public sealed class SearchRouteRef
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("input")]
    public object? Input { get; init; }
}

/// <summary>结果右键菜单动作。</summary>
public sealed class SearchResultAction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("destructive")]
    public bool? Destructive { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("input")]
    public object? Input { get; init; }
}

/// <summary>宿主受控展示元数据。</summary>
public sealed class SearchPresentationMetadata
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

/// <summary>宿主受控展示提示（不能携带自定义 HTML/CSS/脚本）。</summary>
public sealed class SearchPresentation
{
    [JsonPropertyName("variant")]
    public string? Variant { get; init; }

    [JsonPropertyName("badge")]
    public string? Badge { get; init; }

    [JsonPropertyName("badgeTone")]
    public string? BadgeTone { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyList<SearchPresentationMetadata>? Metadata { get; init; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; init; }
}

/// <summary>
/// Provider 单条结果；路由字段（activate/actions.command+input）不下发消费方。
/// <see cref="Icon"/> 为 Brick 根目录相对路径字符串、ResourceRef 或 ResourceHandle；
/// 禁止内嵌 base64。
/// </summary>
public sealed class SearchResultItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("accessory")]
    public string? Accessory { get; init; }

    [JsonPropertyName("icon")]
    public object? Icon { get; init; }

    /// <summary>'app' | 'tool' | 'command' | 'file' | 'shortcut' | 'quick-launch' | 'clipboard' | 'history' | 'other'</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("dedupeKey")]
    public string? DedupeKey { get; init; }

    [JsonPropertyName("activate")]
    public SearchRouteRef? Activate { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<SearchResultAction>? Actions { get; init; }

    [JsonPropertyName("presentation")]
    public SearchPresentation? Presentation { get; init; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

// —— Consumer 侧类型（PlatformService search.*）——

/// <summary>快速搜索消费方请求。trim 后为空的 <see cref="Query"/> 不产生搜索。</summary>
public sealed class SearchQueryRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("sequence")]
    public int? Sequence { get; init; }
}

/// <summary>消费方可见的菜单动作；command/input 留在宿主路由表。</summary>
public sealed class SearchQueryResultAction
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("destructive")]
    public bool? Destructive { get; init; }
}

/// <summary>消费方拿到的单条结果：无激活路由载荷，凭 id 走 Activate/RunAction。</summary>
public sealed class SearchQueryResultItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("brickId")]
    public string? BrickId { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>'app' | 'tool' | 'command'</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("providerLabel")]
    public string? ProviderLabel { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("accessory")]
    public string? Accessory { get; init; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("requiresInput")]
    public bool RequiresInput { get; init; }

    [JsonPropertyName("activatable")]
    public bool Activatable { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<SearchQueryResultAction>? Actions { get; init; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>单 Provider 当次状态。</summary>
public sealed class SearchQueryProviderState
{
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("providerLabel")]
    public string? ProviderLabel { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>'pending' | 'running' | 'complete' | 'failed' | 'skipped' | 'disabled' | 'unavailable'</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>一次查询快照；<see cref="Complete"/>=false 表示仍有 Provider 渐进返回。</summary>
public sealed class SearchQueryResponse
{
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("sequence")]
    public int? Sequence { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<SearchQueryResultItem> Results { get; init; } = [];

    [JsonPropertyName("providerStates")]
    public IReadOnlyList<SearchQueryProviderState> ProviderStates { get; init; } = [];

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("generatedAt")]
    public long GeneratedAt { get; init; }
}

/// <summary>activate / runAction 的结果。</summary>
public sealed class SearchActivationResult
{
    [JsonPropertyName("resultId")]
    public string? ResultId { get; init; }

    /// <summary>'opened-brick' | 'invoked-command' | 'activated-provider-result'</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("resultPreview")]
    public string? ResultPreview { get; init; }
}
