using System.Text.Json;

namespace Syllm.Brickly.Sdk;

/// <summary>子窗 request 进行中的会话。</summary>
public sealed class WindowExposeSession
{
    /// <summary>向子窗推事件。</summary>
    public required Func<object?, Task> EmitAsync { get; init; }

    /// <summary>子窗取消或会话关闭时触发。</summary>
    public required CancellationToken Done { get; init; }
}

/// <summary>处理子窗 notify / request。</summary>
public delegate Task<object?> WindowExposeHandler(object? payload, WindowExposeSession session);

/// <summary>
/// 子窗口句柄，封装 ui.window.call 反射方法。
/// 关闭后再调用（除 IsDestroyedAsync）会立即抛 INVALID_INPUT。
/// </summary>
public partial class WindowHandle : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Action<Dictionary<string, object?>>>> _handlers = new();
    private readonly Dictionary<string, WindowExposeHandler> _exposed = new();
    private readonly Dictionary<string, CancellationTokenSource> _inflight = new();

    private BricklyRuntime? _runtime;
    private bool _closed;
    private Task<WindowRequestCloseResult>? _closeAttempt;
    private Task<WindowTerminationResult>? _forceAttempt;

    internal WindowHandle(
        BricklyRuntime runtime,
        string windowKey,
        long windowId,
        int webContentsId,
        string url,
        string? scopedParentRequestId = null)
    {
        _runtime = runtime;
        WindowKey = windowKey;
        ID = windowId;
        WebContentsID = webContentsId;
        URL = url;
        ScopedParentRequestId = scopedParentRequestId;
    }

    internal string? ScopedParentRequestId { get; }

    public long ID { get; }

    public string WindowKey { get; }

    public int WebContentsID { get; }

    public string URL { get; }

    /// <summary>本地记录的关闭状态（不发协议消息）；权威状态用 IsDestroyedAsync。</summary>
    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>通用调用：走 ui.window.call 白名单，返回原始 JSON。</summary>
    public Task<JsonElement> CallAsync(
        string method,
        IReadOnlyList<object?>? args = null,
        CancellationToken cancellationToken = default) =>
        CallWithParentAsync(method, args, null, cancellationToken);

    /// <summary>通用调用并反序列化结果。</summary>
    public async Task<T> CallAsync<T>(
        string method,
        IReadOnlyList<object?>? args = null,
        CancellationToken cancellationToken = default)
    {
        var element = await CallAsync(method, args, cancellationToken).ConfigureAwait(false);
        return element.Deserialize<T>(JsonDefaults.Options)
            ?? throw new BppException(BppErrorCodes.ProtocolError, $"ui.window.call {method} 返回空结果");
    }

    /// <summary>请求普通关闭；pending / prevented 时句柄保持可用。</summary>
    public async Task<WindowRequestCloseResult> CloseAsync(CancellationToken cancellationToken = default)
    {
        Task<WindowRequestCloseResult> attempt;
        lock (_sync)
        {
            if (_closed)
            {
                return new WindowRequestCloseResult { Status = WindowCloseStatuses.NotFound };
            }
            if (_closeAttempt is not null)
            {
                attempt = _closeAttempt;
            }
            else
            {
                var completion = new TaskCompletionSource<WindowRequestCloseResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _closeAttempt = completion.Task;
                attempt = completion.Task;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        completion.SetResult(await CloseCoreAsync().ConfigureAwait(false));
                    }
                    catch (Exception error)
                    {
                        completion.SetException(error);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _closeAttempt = null;
                        }
                    }
                });
            }
        }
        return await attempt.ConfigureAwait(false);
    }

    /// <summary>强制终止窗口，不经过页面关闭协商。</summary>
    public async Task<WindowTerminationResult> ForceCloseAsync(CancellationToken cancellationToken = default)
    {
        Task<WindowTerminationResult> attempt;
        lock (_sync)
        {
            if (_closed)
            {
                throw new BppException(BppErrorCodes.InvalidInput, "Window already closed");
            }
            if (_forceAttempt is not null)
            {
                attempt = _forceAttempt;
            }
            else
            {
                var completion = new TaskCompletionSource<WindowTerminationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _forceAttempt = completion.Task;
                attempt = completion.Task;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        completion.SetResult(await ForceCloseCoreAsync().ConfigureAwait(false));
                    }
                    catch (Exception error)
                    {
                        completion.SetException(error);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _forceAttempt = null;
                        }
                    }
                });
            }
        }
        return await attempt.ConfigureAwait(false);
    }

    /// <summary>订阅窗口生命周期事件，返回 IDisposable。</summary>
    public IDisposable On(string eventName, Action<Dictionary<string, object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            if (_closed)
            {
                return EmptyDisposable.Instance;
            }
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                list = new List<Action<Dictionary<string, object?>>>();
                _handlers[eventName] = list;
            }
            list.Add(handler);
        }
        return new HandlerSubscription(this, eventName, handler);
    }

    /// <summary>登记子窗可调用的方法；表跟窗走，不跟开窗 command 走。</summary>
    public Task ExposeAsync(IReadOnlyDictionary<string, WindowExposeHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        lock (_sync)
        {
            foreach (var pair in handlers)
            {
                if (pair.Key.StartsWith("brickly:", StringComparison.Ordinal))
                {
                    throw new BppException("RESERVED_NAME", "不能 expose 保留名 " + pair.Key);
                }
                _exposed[pair.Key] = pair.Value;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>登记单个子窗方法。</summary>
    public Task ExposeAsync(string method, WindowExposeHandler handler) =>
        ExposeAsync(new Dictionary<string, WindowExposeHandler> { [method] = handler });

    /// <summary>向子窗推送，不要求 parentRequestId。</summary>
    public async Task SendAsync(string name, object? payload, CancellationToken cancellationToken = default)
    {
        if (name.StartsWith("brickly:", StringComparison.Ordinal))
        {
            throw new BppException("RESERVED_NAME", "不能推送保留名 " + name);
        }
        var runtime = RuntimeOrThrow();
        if (IsClosed)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "window already closed");
        }
        var message = new Dictionary<string, object?>
        {
            ["windowId"] = ID,
            ["name"] = name,
            ["payload"] = payload,
        };
        await runtime.PlatformCallVoidAsync("ui.window.send", message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>webContents 子对象门面。</summary>
    public virtual WebContents WebContents() => new(this, ScopedParentRequestId);

    public void Dispose()
    {
        DisposeLocal(string.Empty);
        GC.SuppressFinalize(this);
    }

    internal async Task<JsonElement> CallWithParentAsync(
        string method,
        IReadOnlyList<object?>? args,
        string? parentRequestId,
        CancellationToken cancellationToken)
    {
        if (method is "close" or "destroy")
        {
            throw new BppException(
                BppErrorCodes.InvalidInput,
                method + " is a lifecycle operation, not a reflected call");
        }

        bool closed;
        BricklyRuntime? runtime;
        lock (_sync)
        {
            closed = _closed;
            runtime = _runtime;
        }
        if (closed && method == "isDestroyed")
        {
            return JsonSerializer.SerializeToElement(true);
        }
        if (closed || runtime is null)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "Window already closed");
        }

        args ??= Array.Empty<object?>();
        if (method == "webContents.send" && string.IsNullOrEmpty(parentRequestId))
        {
            parentRequestId = ExplicitPayloadRequestId(args);
        }
        if (method == "webContents.send" && string.IsNullOrEmpty(parentRequestId))
        {
            throw new BppException(
                BppErrorCodes.ParentInvocationRequired,
                "webContents.Send must run through CommandContext.UI or include payload.requestId");
        }

        var message = new Dictionary<string, object?>
        {
            ["windowId"] = ID,
            ["method"] = method,
            ["args"] = args,
        };
        if (!string.IsNullOrEmpty(parentRequestId))
        {
            message["parentRequestId"] = parentRequestId;
        }
        return await runtime.PlatformCallRawAsync("ui.window.call", message, cancellationToken).ConfigureAwait(false);
    }

    internal void Emit(string eventName, Dictionary<string, object?> payload)
    {
        if (eventName == "closed")
        {
            var terminal = DisposeLocal("closed");
            DispatchHandlers(terminal, payload);
            return;
        }

        Action<Dictionary<string, object?>>[] handlers;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }
            handlers = _handlers.TryGetValue(eventName, out var list)
                ? list.ToArray()
                : Array.Empty<Action<Dictionary<string, object?>>>();
        }
        DispatchHandlers(handlers, payload);
    }

    internal void DispatchChildRpc(string eventName, Dictionary<string, object?> payload)
    {
        switch (eventName)
        {
            case "notify":
            {
                var name = payload.TryGetValue("name", out var nameValue) ? nameValue as string : null;
                WindowExposeHandler? handler;
                lock (_sync)
                {
                    handler = name is not null && _exposed.TryGetValue(name, out var found) ? found : null;
                }
                if (handler is null)
                {
                    return;
                }
                using var done = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(payload.GetValueOrDefault("payload"), new WindowExposeSession
                        {
                            EmitAsync = _ => Task.CompletedTask,
                            Done = done.Token,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // notify 无回复。
                    }
                });
                break;
            }
            case "request":
                DispatchChildRequest(payload);
                break;
            case "request.cancel":
            {
                var requestId = payload.TryGetValue("requestId", out var value) ? value as string : null;
                if (requestId is null)
                {
                    return;
                }
                CancellationTokenSource? source;
                lock (_sync)
                {
                    _inflight.TryGetValue(requestId, out source);
                }
                source?.Cancel();
                break;
            }
        }
    }

    internal Action<Dictionary<string, object?>>[] DisposeLocal(string terminalEvent)
    {
        var runtime = _runtime;
        lock (_sync)
        {
            if (_closed)
            {
                return Array.Empty<Action<Dictionary<string, object?>>>();
            }
            _closed = true;
            _runtime = null;
        }
        runtime?.RemoveWindow(ID, this);

        List<Action<Dictionary<string, object?>>> terminalHandlers = new();
        List<CancellationTokenSource> inflight;
        lock (_sync)
        {
            if (terminalEvent.Length > 0 && _handlers.TryGetValue(terminalEvent, out var list))
            {
                terminalHandlers.AddRange(list);
            }
            _handlers.Clear();
            _exposed.Clear();
            inflight = _inflight.Values.ToList();
            _inflight.Clear();
        }
        foreach (var source in inflight)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return terminalHandlers.ToArray();
    }

    private async Task<WindowRequestCloseResult> CloseCoreAsync()
    {
        var runtime = RuntimeOrThrow();
        var result = await runtime
            .PlatformCallAsync<WindowRequestCloseResult>(
                "ui.window.requestClose",
                new Dictionary<string, object?> { ["windowId"] = ID },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!ValidCloseStatus(result.Status))
        {
            throw new BppException(BppErrorCodes.ProtocolError, "ui.window.requestClose returned an invalid result");
        }
        if (result.Status is WindowCloseStatuses.Closed or WindowCloseStatuses.NotFound)
        {
            DisposeLocal(string.Empty);
        }
        return result;
    }

    private async Task<WindowTerminationResult> ForceCloseCoreAsync()
    {
        var runtime = RuntimeOrThrow();
        var result = await runtime
            .PlatformCallAsync<WindowTerminationResult>(
                "ui.window.forceClose",
                new Dictionary<string, object?> { ["windowId"] = ID },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!ValidTerminationResult(result))
        {
            throw new BppException(BppErrorCodes.ProtocolError, "ui.window.forceClose returned an invalid result");
        }
        DisposeLocal(string.Empty);
        return result;
    }

    private void DispatchChildRequest(Dictionary<string, object?> payload)
    {
        var name = payload.TryGetValue("name", out var nameValue) ? nameValue as string : null;
        var requestId = payload.TryGetValue("requestId", out var idValue) ? idValue as string : null;
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        WindowExposeHandler? handler;
        lock (_sync)
        {
            handler = name is not null && _exposed.TryGetValue(name, out var found) ? found : null;
        }
        if (handler is null)
        {
            _ = ChildReplyAsync(requestId, false, null, "NOT_EXPOSED", $"未 expose \"{name}\"");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _inflight[requestId] = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                object? result;
                try
                {
                    result = await handler(payload.GetValueOrDefault("payload"), new WindowExposeSession
                    {
                        EmitAsync = eventValue => EmitChildEventAsync(requestId, eventValue),
                        Done = cts.Token,
                    }).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var code = error is BppException bpp ? bpp.Code : "INTERNAL_ERROR";
                    await ChildReplyAsync(requestId, false, null, code, error.Message).ConfigureAwait(false);
                    return;
                }
                await ChildReplyAsync(requestId, true, result, string.Empty, string.Empty).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _inflight.Remove(requestId);
                }
                cts.Dispose();
            }
        });
    }

    private Task EmitChildEventAsync(string requestId, object? eventValue)
    {
        var runtime = _runtime;
        if (runtime is null)
        {
            return Task.CompletedTask;
        }
        return runtime.PlatformCallVoidAsync("ui.window.emit", new Dictionary<string, object?>
        {
            ["windowId"] = ID,
            ["requestId"] = requestId,
            ["event"] = eventValue,
        }, CancellationToken.None);
    }

    private async Task ChildReplyAsync(string requestId, bool ok, object? result, string code, string message)
    {
        var runtime = _runtime;
        if (runtime is null)
        {
            return;
        }
        object? error = ok
            ? null
            : new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        try
        {
            await runtime.PlatformCallVoidAsync("ui.window.reply", new Dictionary<string, object?>
            {
                ["windowId"] = ID,
                ["requestId"] = requestId,
                ["ok"] = ok,
                ["result"] = result,
                ["error"] = error,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 窗可能已销毁。
        }
    }

    private BricklyRuntime RuntimeOrThrow()
    {
        lock (_sync)
        {
            if (_runtime is null)
            {
                throw new BppException(BppErrorCodes.InvalidInput, "Window already closed");
            }
            return _runtime;
        }
    }

    private void RemoveHandler(string eventName, Action<Dictionary<string, object?>> handler)
    {
        lock (_sync)
        {
            if (_handlers.TryGetValue(eventName, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _handlers.Remove(eventName);
                }
            }
        }
    }

    private static void DispatchHandlers(
        Action<Dictionary<string, object?>>[] handlers,
        Dictionary<string, object?> payload)
    {
        foreach (var handler in handlers)
        {
            var captured = handler;
            _ = Task.Run(() =>
            {
                try
                {
                    captured(payload);
                }
                catch (Exception)
                {
                    // 订阅者异常不传播。
                }
            });
        }
    }

    private static bool ValidCloseStatus(string status) =>
        status is WindowCloseStatuses.Closed or WindowCloseStatuses.Prevented
            or WindowCloseStatuses.Pending or WindowCloseStatuses.NotFound;

    private static bool ValidTerminationResult(WindowTerminationResult result)
    {
        var validEvent = result.Event is WindowTerminalEventStatuses.Sent
            or WindowTerminalEventStatuses.Skipped
            or WindowTerminalEventStatuses.Failed;
        var validWindow = result.Window is WindowNativeStatuses.Destroyed
            or WindowNativeStatuses.AlreadyDestroyed
            or WindowNativeStatuses.Failed;
        return validEvent && validWindow && result.Errors is not null;
    }

    private static string ExplicitPayloadRequestId(IReadOnlyList<object?> args)
    {
        if (args.Count < 2 || args[1] is not IReadOnlyDictionary<string, object?> payload)
        {
            return string.Empty;
        }
        return payload.TryGetValue("requestId", out var value) && value is string requestId ? requestId : string.Empty;
    }

    private sealed class HandlerSubscription : IDisposable
    {
        private readonly WindowHandle _handle;
        private readonly string _eventName;
        private readonly Action<Dictionary<string, object?>> _handler;
        private bool _disposed;

        public HandlerSubscription(
            WindowHandle handle,
            string eventName,
            Action<Dictionary<string, object?>> handler)
        {
            _handle = handle;
            _eventName = eventName;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _handle.RemoveHandler(_eventName, _handler);
        }
    }

    internal sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>webContents 子对象门面。</summary>
public class WebContents
{
    private readonly WindowHandle _window;
    private readonly string? _parentRequestId;

    internal WebContents(WindowHandle window, string? parentRequestId = null)
    {
        _window = window;
        _parentRequestId = parentRequestId;
    }

    public Task SendAsync(string channel, params object?[] args)
    {
        var list = new List<object?> { channel };
        list.AddRange(args);
        return _window.CallWithParentAsync("webContents.send", list, _parentRequestId, CancellationToken.None);
    }

    public Task<JsonElement> ExecuteJavaScriptAsync(string code, bool? userGesture = null, CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { code };
        if (userGesture is not null)
        {
            args.Add(userGesture.Value);
        }
        return _window.CallWithParentAsync("webContents.executeJavaScript", args, _parentRequestId, cancellationToken);
    }

    public Task OpenDevToolsAsync(OpenDevToolsOptions? options = null, CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync(
            "webContents.openDevTools",
            options is null ? Array.Empty<object?>() : new object?[] { options },
            _parentRequestId,
            cancellationToken);

    public Task CloseDevToolsAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.closeDevTools", null, _parentRequestId, cancellationToken);

    public Task ToggleDevToolsAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.toggleDevTools", null, _parentRequestId, cancellationToken);

    public Task<bool> IsDevToolsOpenedAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<bool>("webContents.isDevToolsOpened", null, cancellationToken);

    public Task GoBackAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.goBack", null, _parentRequestId, cancellationToken);

    public Task GoForwardAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.goForward", null, _parentRequestId, cancellationToken);

    public Task<bool> CanGoBackAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<bool>("webContents.canGoBack", null, cancellationToken);

    public Task<bool> CanGoForwardAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<bool>("webContents.canGoForward", null, cancellationToken);

    public Task<string> GetUrlAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<string>("webContents.getURL", null, cancellationToken);

    public Task<string> GetTitleAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<string>("webContents.getTitle", null, cancellationToken);

    public Task SetZoomFactorAsync(double factor, CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.setZoomFactor", new object?[] { factor }, _parentRequestId, cancellationToken);

    public Task<double> GetZoomFactorAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<double>("webContents.getZoomFactor", null, cancellationToken);

    public Task SetZoomLevelAsync(double level, CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.setZoomLevel", new object?[] { level }, _parentRequestId, cancellationToken);

    public Task<double> GetZoomLevelAsync(CancellationToken cancellationToken = default) =>
        _window.CallAsync<double>("webContents.getZoomLevel", null, cancellationToken);

    public Task CopyAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.copy", null, _parentRequestId, cancellationToken);

    public Task PasteAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.paste", null, _parentRequestId, cancellationToken);

    public Task CutAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.cut", null, _parentRequestId, cancellationToken);

    public Task SelectAllAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.selectAll", null, _parentRequestId, cancellationToken);

    public Task UndoAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.undo", null, _parentRequestId, cancellationToken);

    public Task RedoAsync(CancellationToken cancellationToken = default) =>
        _window.CallWithParentAsync("webContents.redo", null, _parentRequestId, cancellationToken);
}
