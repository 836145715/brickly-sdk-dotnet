using System.Threading.Channels;

namespace Syllm.Brickly.Sdk;

/// <summary>
/// interact 返回的领域会话。调用方不接触 gRPC 类型。
/// RequestAsync 取消只停这一条（等同 Node pending.cancel()），会话仍可继续。
/// </summary>
public interface Interaction
{
    /// <summary>推事件给调用方。</summary>
    Task SendAsync(object? eventValue, CancellationToken cancellationToken = default);

    /// <summary>按 key 推「最新」事件；.NET 实现不做队列合并，直接发送。</summary>
    Task SendLatestAsync(string key, object? eventValue, CancellationToken cancellationToken = default);

    /// <summary>等这一条回复；取消传入的 token 只停这一条。</summary>
    Task<object?> RequestAsync(object? request, CancellationToken cancellationToken = default);

    /// <summary>半关闭输入并等待最终结果。</summary>
    Task<object?> EndAsync(CancellationToken cancellationToken = default);

    /// <summary>半关闭输入并等待最终结果，超时返回 DEADLINE_EXCEEDED。</summary>
    Task<object?> EndAsync(int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>整条会话取消。</summary>
    void Cancel(string reason);
}

/// <summary>interact 参数；OnEvent 必须传入。</summary>
public sealed class InteractOptions
{
    /// <summary>收调用方事件；必须非空。</summary>
    public required Action<object?> OnEvent { get; init; }
}

/// <summary>CallAsync 参数；OnEvent 必须传入。</summary>
public sealed class CallOptions
{
    public required Action<object?> OnEvent { get; init; }

    /// <summary>等待最终结果的超时（毫秒）；空表示不超时。</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>只暴露 invoke / interact 两种协议形态；CallAsync 是糖。</summary>
public interface IBrickClient
{
    Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default);

    Task<Interaction> InteractAsync(
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>实现层半关闭与事件读取面，不出现在公共文档。</summary>
internal interface IInteractionTransport : Interaction
{
    Task CloseInputAsync(CancellationToken cancellationToken);

    ChannelReader<object?> EventReader { get; }

    Task<object?> ResultAsync();
}

internal static class InteractionSupport
{
    public static void RequireOnEvent(InteractOptions? options)
    {
        if (options is null || options.OnEvent is null)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "interact 必须传入 OnEvent");
        }
    }

    public static void RequireOnEvent(CallOptions? options)
    {
        if (options is null || options.OnEvent is null)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "call 必须传入 OnEvent");
        }
    }

    public static void Pump(Interaction session, Action<object?> onEvent)
    {
        if (session is not IInteractionTransport transport || onEvent is null)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in transport.EventReader.ReadAllAsync().ConfigureAwait(false))
                {
                    onEvent(item);
                }
            }
            catch (Exception)
            {
                // 会话结束；事件泵静默退出。
            }
        });
    }

    /// <summary>CallAsync = Interact + 立刻 End。</summary>
    public static async Task<object?> CallAsync(
        IBrickClient client,
        string commandId,
        object? input,
        CallOptions options,
        CancellationToken cancellationToken = default)
    {
        RequireOnEvent(options);
        var session = await client
            .InteractAsync(commandId, input, new InteractOptions { OnEvent = options.OnEvent }, cancellationToken)
            .ConfigureAwait(false);
        if (options.TimeoutMs is int timeoutMs)
        {
            return await session.EndAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        return await session.EndAsync(cancellationToken).ConfigureAwait(false);
    }
}
