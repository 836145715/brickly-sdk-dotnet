using System.Text.Json;

namespace Syllm.Brickly.Sdk;

/// <summary>
/// OnSearch 处理函数的上下文：组合 CommandContext 并带固定协议入参。
/// 宿主调用前已 trim query 并完成空查询硬门槛，handler 收到的一定是非空文本。
/// </summary>
public sealed class SearchContext
{
    internal SearchContext(
        CommandContext context,
        string query,
        int sequence,
        int limit,
        SearchCaller? caller)
    {
        Command = context;
        Query = query;
        Sequence = sequence;
        Limit = limit;
        Caller = caller;
    }

    /// <summary>底层命令上下文（send/onEvent/handleRequests/closed 等会话能力）。</summary>
    public CommandContext Command { get; }

    /// <summary>本次调用请求 id。</summary>
    public string RequestID => Command.RequestID;

    /// <summary>provider 端点命令 id（manifest commands[].id）。</summary>
    public string CommandID => Command.CommandID;

    /// <summary>trim 后的查询文本，非空。</summary>
    public string Query { get; }

    /// <summary>消费方发起的第 N 次查询序号。</summary>
    public int Sequence { get; }

    /// <summary>消费方请求的结果上限（与 tuning maxResults 取小生效）。</summary>
    public int Limit { get; }

    /// <summary>消费方身份。</summary>
    public SearchCaller? Caller { get; }

    /// <summary>新查询发起或会话结束时宿主置位；应尽早停止产出。</summary>
    public CancellationToken CancellationToken => Command.CancellationToken;

    public bool IsCancelled => Command.IsCancelled;

    public IReadOnlyDictionary<string, object?> Config => Command.Config;

    public PlatformApi Platform() => Command.Platform();

    public SystemApi System() => Command.System();

    public StorageApi Storage() => Command.Storage();

    public ScopedDependencyRegistry Dependencies() => Command.Dependencies();

    public EventBus Events => Command.Events;

    public void Debug(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Command.Debug(message, fields);

    public void Info(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Command.Info(message, fields);

    public void Warn(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Command.Warn(message, fields);

    public void Error(string message, Exception? error = null, IReadOnlyDictionary<string, object?>? fields = null) =>
        Command.Error(message, error, fields);
}

/// <summary>
/// OnSearch 注册的业务处理函数；返回结果数组，SDK 归一为 { results: [...] } 回传宿主。
/// </summary>
public delegate Task<IReadOnlyList<SearchResultItem>> SearchHandler(SearchContext context);

/// <summary>
/// OnSearch → OnCommand 的适配壳：解固定入参、收 handler 返回成 {results}。
/// 输入/输出畸形直接抛 BppException —— 宿主按 failed 记录，该 Provider 返回空结果。
/// </summary>
internal static class SearchBinding
{
    public static CommandHandler Adapt(SearchHandler handler) => async (context, input) =>
    {
        var endpoint = ParseEndpointInput(input);
        var searchContext = new SearchContext(
            context,
            endpoint.Query,
            endpoint.Sequence,
            endpoint.Limit,
            endpoint.Caller);
        var results = await handler(searchContext).ConfigureAwait(false);
        if (results is null)
        {
            throw new BppException(BppErrorCodes.Internal, "onSearch 返回值必须是结果数组");
        }
        foreach (var item in results)
        {
            ValidateResultItem(item);
        }
        return new Dictionary<string, object?> { ["results"] = results };
    };

    private readonly record struct EndpointInput(
        string Query,
        int Sequence,
        int Limit,
        SearchCaller? Caller);

    /// <summary>
    /// 宿主固定入参 {providerId,query,sequence,limit,caller}；query 已 trim 非空。
    /// 与 Node/Go/Python SDK 同口径：sequence/limit 非数值回退默认，
    /// caller 畸形宽容为 null 而非整体报错。
    /// </summary>
    private static EndpointInput ParseEndpointInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "搜索 Provider 入参必须是对象");
        }
        if (!input.TryGetProperty("query", out var queryElement) ||
            queryElement.ValueKind != JsonValueKind.String)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "搜索 Provider 入参缺少 query 字符串");
        }
        return new EndpointInput(
            queryElement.GetString() ?? string.Empty,
            ReadCount(input, "sequence", 0),
            ReadCount(input, "limit", 24),
            ReadCaller(input));
    }

    private static int ReadCount(JsonElement input, string name, int fallback)
    {
        if (!input.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }
        return element.TryGetInt32(out var value) ? value : fallback;
    }

    private static SearchCaller? ReadCaller(JsonElement input)
    {
        if (!input.TryGetProperty("caller", out var element) ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("brickId", out var brickId) ||
            brickId.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return new SearchCaller
        {
            BrickId = brickId.GetString() ?? string.Empty,
            Origin = ReadString(element, "origin"),
            Version = ReadString(element, "version"),
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void ValidateResultItem(SearchResultItem item)
    {
        // 与 Node/Go/Python SDK 统一口径：id/title 必须非空（宿主对空值项另有过滤兜底）。
        if (string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.Title))
        {
            throw new BppException(BppErrorCodes.Internal, "搜索结果项必须含非空 id 与 title");
        }
        switch (item.Icon)
        {
            case null or string:
                return;
            case ResourceRef reference:
                if (!reference.IsValid())
                {
                    throw new BppException(BppErrorCodes.Internal, "搜索结果 icon 的 ResourceRef 无效");
                }
                return;
            case ResourceHandle handle:
                if (!handle.Ref.IsValid())
                {
                    throw new BppException(BppErrorCodes.Internal, "搜索结果 icon 的 ResourceRef 无效");
                }
                return;
            default:
                throw new BppException(BppErrorCodes.Internal, "搜索结果 icon 只接受相对路径或 ResourceRef");
        }
    }
}
