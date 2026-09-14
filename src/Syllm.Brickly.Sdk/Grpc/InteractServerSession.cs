using System.Text.Json;
using System.Threading.Channels;
using Brickly.Runtime.V1;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>
/// 服务端 interact 会话：帧顺序校验、事件推送、会话内 request 并发与单条取消。
/// 语义与 Go SDK 的 interactServerSession 对齐。
/// </summary>
internal sealed class InteractServerSession
{
    public const int DefaultRequestConcurrency = 8;
    public const int MaxRequestConcurrency = 128;

    private readonly Channel<object?> _events;
    private readonly Func<ServerFrame, Task> _write;
    private readonly CancellationTokenSource _sessionCts;
    private readonly TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private readonly List<Task> _requestTasks = new();

    private Func<object?, CancellationToken, Task<object?>>? _requestHandler;
    private SemaphoreSlim? _slots;
    private Dictionary<string, CancellationTokenSource>? _inflight;
    private bool _closedInput;
    private bool _stopped;

    public InteractServerSession(JsonElement initial, CancellationToken contextToken, Func<ServerFrame, Task> write)
    {
        Initial = initial;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(contextToken);
        _events = Channel.CreateBounded<object?>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _write = write;
    }

    public JsonElement Initial { get; }

    /// <summary>Host 在 stream metadata 中注入的 invocationId。</summary>
    public string? InvocationId { get; set; }

    public CancellationToken ContextToken => _sessionCts.Token;

    public ChannelReader<object?> Events => _events.Reader;

    /// <summary>调用方 end / 断开后完成。</summary>
    public Task Closed => _closed.Task;

    public async Task SendAsync(object? value)
    {
        var payload = BrickValueCodec.FromClr(value);
        await _write(new ServerFrame { Event = new EventFrame { Payload = payload } }).ConfigureAwait(false);
    }

    public void HandleRequests(Func<object?, CancellationToken, Task<object?>> handler, int? concurrency = null)
    {
        var slots = concurrency ?? DefaultRequestConcurrency;
        if (slots < 1 || slots > MaxRequestConcurrency)
        {
            throw new BppException(BppErrorCodes.InvalidInput, "HandleRequests concurrency 必须是 1–128");
        }

        lock (_sync)
        {
            if (_requestHandler is not null)
            {
                throw new BppException(BppErrorCodes.ProtocolError, "一个 session 只能注册一次 request handler");
            }
            _requestHandler = handler;
            _slots = new SemaphoreSlim(slots, slots);
            _inflight = new Dictionary<string, CancellationTokenSource>();
        }
    }

    public async Task PushAsync(object? value)
    {
        lock (_sync)
        {
            if (_closedInput)
            {
                return;
            }
        }
        try
        {
            await _events.Writer.WriteAsync(value, _sessionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    public void CloseInput()
    {
        lock (_sync)
        {
            if (_closedInput)
            {
                return;
            }
            _closedInput = true;
            _events.Writer.TryComplete();
        }
        _closed.TrySetResult();
    }

    public void CancelRequest(byte[] messageId)
    {
        CancellationTokenSource? source;
        lock (_sync)
        {
            source = null;
            _inflight?.TryGetValue(Key(messageId), out source);
        }
        source?.Cancel();
    }

    public void OnRequest(BrickValue payload, byte[] replyTo)
    {
        var task = Task.Run(() => DispatchRequestAsync(payload, replyTo));
        lock (_sync)
        {
            _requestTasks.Add(task);
        }
    }

    /// <summary>取消全部在途 request 并等待其结束；final 前调用。</summary>
    public async Task SettleRequestsAsync()
    {
        List<Task> tasks;
        lock (_sync)
        {
            _stopped = true;
            if (_inflight is not null)
            {
                foreach (var source in _inflight.Values)
                {
                    source.Cancel();
                }
            }
            tasks = new List<Task>(_requestTasks);
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 单条 request 的失败已经写回 ResponseFrame，这里只等待收敛。
        }
    }

    private async Task DispatchRequestAsync(BrickValue payload, byte[] replyTo)
    {
        Func<object?, CancellationToken, Task<object?>>? handler;
        bool stopped;
        SemaphoreSlim? slots;
        lock (_sync)
        {
            handler = _requestHandler;
            stopped = _stopped;
            slots = _slots;
        }

        if (handler is null)
        {
            await WriteResponseAsync(replyTo, null, RequestBrickError("REQUEST_HANDLER_UNAVAILABLE", "未注册 request handler"))
                .ConfigureAwait(false);
            return;
        }
        if (stopped)
        {
            await WriteResponseAsync(replyTo, null, RequestBrickError("CANCELLED", "request 已取消")).ConfigureAwait(false);
            return;
        }

        var key = Key(replyTo);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        lock (_sync)
        {
            _inflight ??= new Dictionary<string, CancellationTokenSource>();
            _inflight[key] = requestCts;
        }

        try
        {
            var requestSlots = slots ?? new SemaphoreSlim(DefaultRequestConcurrency, DefaultRequestConcurrency);
            try
            {
                await requestSlots.WaitAsync(requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await WriteResponseAsync(replyTo, null, RequestBrickError("CANCELLED", "request 已取消")).ConfigureAwait(false);
                return;
            }

            try
            {
                if (requestCts.IsCancellationRequested)
                {
                    await WriteResponseAsync(replyTo, null, RequestBrickError("CANCELLED", "request 已取消")).ConfigureAwait(false);
                    return;
                }

                object? result;
                try
                {
                    result = await handler(BrickValueCodec.ToClr(payload), requestCts.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await WriteResponseAsync(replyTo, null, EncodeRequestError(error, requestCts.IsCancellationRequested))
                        .ConfigureAwait(false);
                    return;
                }

                BrickValue value;
                try
                {
                    value = BrickValueCodec.FromClr(result);
                }
                catch (Exception error)
                {
                    await WriteResponseAsync(replyTo, null, EncodeRequestError(error, false)).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(replyTo, value, null).ConfigureAwait(false);
            }
            finally
            {
                requestSlots.Release();
            }
        }
        finally
        {
            lock (_sync)
            {
                _inflight?.Remove(key);
            }
        }
    }

    private Task WriteResponseAsync(byte[] replyTo, BrickValue? value, BrickError? error)
    {
        var frame = new ServerFrame
        {
            Header = new FrameHeader { ReplyTo = Google.Protobuf.ByteString.CopyFrom(replyTo) },
        };
        if (error is not null)
        {
            frame.Response = new ResponseFrame { Error = error };
        }
        else
        {
            frame.Response = new ResponseFrame { Value = value ?? BrickValueCodec.NullValue() };
        }
        return _write(frame);
    }

    private static BrickError EncodeRequestError(Exception error, bool cancelled)
    {
        if (cancelled || error is OperationCanceledException)
        {
            return RequestBrickError("CANCELLED", "request 已取消");
        }
        var code = error is BppException bpp ? NormalizeRequestErrorCode(bpp.Code) : "INTERNAL";
        return RequestBrickError(code, error.Message);
    }

    private static string NormalizeRequestErrorCode(string code) => code switch
    {
        "INTERNAL_ERROR" => "INTERNAL",
        "PROTOCOL_ERROR" => "PROTOCOL_VIOLATION",
        "" => "INTERNAL",
        _ => code,
    };

    private static BrickError RequestBrickError(string code, string message)
    {
        return new BrickError
        {
            Code = code,
            Message = BrickErrorStatus.Sanitize(message),
            Retryable = false,
        };
    }

    private static string Key(byte[] messageId) => Convert.ToHexString(messageId);
}
