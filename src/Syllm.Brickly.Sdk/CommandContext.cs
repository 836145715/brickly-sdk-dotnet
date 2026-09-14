using System.Text.Json;
using System.Threading.Channels;
using Syllm.Brickly.Sdk.Grpc;

namespace Syllm.Brickly.Sdk;

/// <summary>命令处理器：返回结果或抛 BppException（保留 code 回传宿主）。</summary>
public delegate Task<object?> CommandHandler(CommandContext context, JsonElement input);

/// <summary>会话内 request handler；返回值就是那条 request 的结果。</summary>
public delegate Task<object?> RequestHandler(object? request, CancellationToken cancellationToken);

/// <summary>命令流抽象：invoke 路径的 send / onEvent / handleRequests 一律 PROTOCOL_ERROR。</summary>
internal interface ICommandStream
{
    Task SendAsync(object? eventValue);

    void OnEvent(Action<object?> handler);

    void HandleRequests(RequestHandler handler, int? concurrency);

    Task Closed { get; }
}

internal sealed class UnaryCommandStream : ICommandStream
{
    public static readonly UnaryCommandStream Instance = new();

    public Task SendAsync(object? eventValue) =>
        throw new BppException(BppErrorCodes.ProtocolError, "send 需要 interact 调用；本次是 invoke");

    public void OnEvent(Action<object?> handler) =>
        throw new BppException(BppErrorCodes.ProtocolError, "onEvent 需要 interact 调用；本次是 invoke");

    public void HandleRequests(RequestHandler handler, int? concurrency) =>
        throw new BppException(BppErrorCodes.ProtocolError, "handleRequests 需要 interact 调用；本次是 invoke");

    public Task Closed => Task.CompletedTask;
}

internal sealed class InteractCommandStream : ICommandStream
{
    private readonly InteractServerSession _session;
    private readonly object _sync = new();
    private readonly List<object?> _pending = new();
    private Action<object?>? _handler;

    public InteractCommandStream(InteractServerSession session)
    {
        _session = session;
        _ = Task.Run(PumpAsync);
    }

    public Task Closed => _session.Closed;

    public Task SendAsync(object? eventValue) => _session.SendAsync(eventValue);

    public void OnEvent(Action<object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        List<object?> pending;
        lock (_sync)
        {
            if (_handler is not null)
            {
                throw new BppException(BppErrorCodes.ProtocolError, "onEvent 只能注册一次");
            }
            _handler = handler;
            pending = new List<object?>(_pending);
            _pending.Clear();
        }
        foreach (var item in pending)
        {
            handler(item);
        }
    }

    public void HandleRequests(RequestHandler handler, int? concurrency)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _session.HandleRequests(
            (request, cancellationToken) => handler(request, cancellationToken),
            concurrency);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in _session.Events.ReadAllAsync(_session.ContextToken).ConfigureAwait(false))
            {
                Action<object?>? handler;
                lock (_sync)
                {
                    handler = _handler;
                    if (handler is null)
                    {
                        _pending.Add(item);
                        continue;
                    }
                }
                handler(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }
}

/// <summary>
/// CommandHandler 的第一个参数。Context() 语义由 <see cref="CancellationToken"/> 承担；
/// IsCancelled 提供协作式取消轮询。
/// </summary>
public sealed class CommandContext
{
    private readonly BricklyRuntime _runtime;
    private readonly ICommandStream _stream;
    private readonly CancellationToken _cancellationToken;

    internal CommandContext(
        BricklyRuntime runtime,
        string requestId,
        string commandId,
        CommandInvocationContext invocation,
        TraceContext? trace,
        CancellationToken cancellationToken,
        ICommandStream stream)
    {
        _runtime = runtime;
        _stream = stream;
        _cancellationToken = cancellationToken;
        RequestID = requestId;
        CommandID = commandId;
        Invocation = invocation;
        Trace = trace;
    }

    public string RequestID { get; }

    public string CommandID { get; }

    /// <summary>宿主注入的可信调用来源；未提供时 Source 为 unknown。</summary>
    public CommandInvocationContext Invocation { get; }

    public TraceContext? Trace { get; }

    /// <summary>命令取消时被取消；可直接传给下游 API。</summary>
    public CancellationToken CancellationToken => _cancellationToken;

    public bool IsCancelled => _cancellationToken.IsCancellationRequested;

    /// <summary>当前 Profile 配置快照。</summary>
    public IReadOnlyDictionary<string, object?> Config => _runtime.Config;

    /// <summary>推事件给调用方（仅 interact）。</summary>
    public Task SendAsync(object? eventValue) => _stream.SendAsync(eventValue);

    /// <summary>收调用方事件（仅 interact）；一个命令只能注册一次。</summary>
    public void OnEvent(Action<object?> handler) => _stream.OnEvent(handler);

    /// <summary>注册会话内 request handler（仅 interact）；return 就是那条 request 的结果。</summary>
    public void HandleRequests(RequestHandler handler, int? concurrency = null) =>
        _stream.HandleRequests(handler, concurrency);

    /// <summary>调用方 end / 断开后完成；invoke 路径已经完成。</summary>
    public Task Closed => _stream.Closed;

    /// <summary>命令作用域 UI 门面；窗口在 webContents.Send 时携带当前 RequestID。</summary>
    public ScopedUI UI() => new(_runtime, RequestID, Trace);

    /// <summary>Brick 级事件总线（与 Runtime.Events 同源）。</summary>
    public EventBus Events => _runtime.Events;

    /// <summary>宿主平台能力门面，携带当前 command 的 trace。</summary>
    public PlatformApi Platform() => _runtime.Platform;

    /// <summary>宿主系统能力门面。</summary>
    public SystemApi System() => _runtime.System;

    /// <summary>本机持久存储。</summary>
    public StorageApi Storage() => _runtime.Storage;

    /// <summary>绑定当前 command parent、trace 与 Profile 的依赖注册表。</summary>
    public ScopedDependencyRegistry Dependencies() =>
        new(_runtime.Dependencies, RequestID, Trace, Invocation.DependencyProfiles);

    public Task<ResourceHandle> CreateResourceAsync(object content, ResourceCreateOptions? options = null)
        => _runtime.CreateResourceAsync(content, options, RequestID);

    public Task<ResourceHandle> CreateResourceFromAsync(Stream source, ResourceCreateOptions? options = null)
        => _runtime.CreateResourceFromAsync(source, options, RequestID);

    public Task<ResourceWriter> CreateResourceWriterAsync(ResourceCreateOptions? options = null)
        => _runtime.CreateResourceWriterAsync(options, RequestID);

    public void Debug(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        _runtime.EmitLog("debug", message, null, fields, RequestID);

    public void Info(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        _runtime.EmitLog("info", message, null, fields, RequestID);

    public void Warn(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        _runtime.EmitLog("warn", message, null, fields, RequestID);

    public void Error(string message, Exception? error = null, IReadOnlyDictionary<string, object?>? fields = null) =>
        _runtime.EmitLog("error", message, error, fields, RequestID);
}
