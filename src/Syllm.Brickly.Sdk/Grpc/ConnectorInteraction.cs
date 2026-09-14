using System.Threading.Channels;
using Brickly.Runtime.V1;
using Google.Protobuf;
using Grpc.Core;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>
/// Runtime → Host 的 interact 客户端会话：sequence 严格递增、request/reply 关联、
/// 单条取消写 cancel_request、final 结算。语义与 Go SDK 的 ConnectorInteraction 对齐。
/// </summary>
internal sealed class ConnectorInteraction : IInteractionTransport
{
    private enum RequestState
    {
        Queued,
        Sent,
        Settled,
        Cancelled,
    }

    private sealed class PendingRequest
    {
        public required byte[] MessageId { get; init; }

        public RequestState State { get; set; } = RequestState.Queued;

        public TaskCompletionSource<ResponseOutcome> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ResponseOutcome(object? Value, Exception? Error);

    private readonly AsyncDuplexStreamingCall<ClientFrame, ServerFrame> _stream;
    private readonly CancellationTokenSource _callCts;
    private readonly Channel<object?> _events;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingRequest> _pending = new();

    private ulong _outbound;
    private ulong _inbound = 1;
    private bool _closed;
    private bool _peerFinal;
    private bool _inputClosed;
    private Exception? _error;
    private object? _result;

    public ConnectorInteraction(
        AsyncDuplexStreamingCall<ClientFrame, ServerFrame> stream,
        CancellationTokenSource callCts)
    {
        _stream = stream;
        _callCts = callCts;
        _events = Channel.CreateBounded<object?>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelReader<object?> EventReader => _events.Reader;

    public async Task OpenAsync(string commandId, BrickValue input)
    {
        await WriteFrameAsync(
            new ClientFrame { Open = new OpenFrame { CommandId = commandId, Input = input } },
            CancellationToken.None).ConfigureAwait(false);

        if (!await _stream.ResponseStream.MoveNext(_callCts.Token).ConfigureAwait(false))
        {
            throw new BppException(BppErrorCodes.ProtocolError, "PROTOCOL_VIOLATION: 服务端首帧必须是 opened");
        }
        _inbound = 1;
        if (_stream.ResponseStream.Current.Opened is null)
        {
            throw new BppException(BppErrorCodes.ProtocolError, "PROTOCOL_VIOLATION: 服务端首帧必须是 opened");
        }
    }

    public void StartReadLoop() => _ = Task.Run(ReadLoopAsync);

    public async Task SendAsync(object? eventValue, CancellationToken cancellationToken = default)
    {
        AssertWritable();
        var payload = BrickValueCodec.FromClr(eventValue);
        await WriteFrameAsync(
            new ClientFrame { Event = new EventFrame { Payload = payload } },
            cancellationToken).ConfigureAwait(false);
    }

    public Task SendLatestAsync(string key, object? eventValue, CancellationToken cancellationToken = default)
    {
        _ = key;
        return SendAsync(eventValue, cancellationToken);
    }

    public async Task<object?> RequestAsync(object? request, CancellationToken cancellationToken = default)
    {
        AssertWritable();
        cancellationToken.ThrowIfCancellationRequested();

        var payload = BrickValueCodec.FromClr(request);
        var messageId = RandomMessageId();
        var entry = new PendingRequest { MessageId = messageId };
        lock (_sync)
        {
            if (_inputClosed || _error is not null || _closed)
            {
                throw new BppException(BppErrorCodes.Cancelled, "interaction 已不能发送");
            }
            _pending[Key(messageId)] = entry;
            entry.State = RequestState.Sent;
        }

        try
        {
            var frame = new ClientFrame
            {
                Header = new FrameHeader { MessageId = ByteString.CopyFrom(messageId) },
                Request = new RequestFrame { Payload = payload },
            };
            await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                FinishPendingLocked(entry, error);
            }
            throw;
        }

        using var registration = cancellationToken.Register(
            () => CancelPending(entry, new OperationCanceledException(cancellationToken)));

        var outcome = await entry.Reply.Task.ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            throw outcome.Error;
        }
        return outcome.Value;
    }

    public async Task<object?> EndAsync(CancellationToken cancellationToken = default)
    {
        await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
        return await ResultAsync().ConfigureAwait(false);
    }

    public async Task<object?> EndAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            return await ResultAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Cancel("CANCELLED");
                await ResultAsync().ConfigureAwait(false);
                throw;
            }
            Cancel("DEADLINE_EXCEEDED");
            await ResultAsync().ConfigureAwait(false);
            throw new BppException("DEADLINE_EXCEEDED", "end 等待超时");
        }
    }

    public void Cancel(string reason)
    {
        _ = reason;
        Fail(new BppException(BppErrorCodes.Cancelled, "interaction 已取消"));
        try
        {
            _callCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task CloseInputAsync(CancellationToken cancellationToken)
    {
        List<ClientFrame> cancels = new();
        lock (_sync)
        {
            if (_inputClosed || _error is not null || _closed)
            {
                return;
            }
            foreach (var entry in _pending.Values)
            {
                if (entry.State == RequestState.Sent && !_peerFinal && _error is null)
                {
                    cancels.Add(CancelRequestFrame(entry.MessageId));
                }
                FinishPendingLocked(entry, new BppException(BppErrorCodes.Cancelled, "SESSION_CLOSED: interaction 已取消"));
            }
            _inputClosed = true;
        }

        foreach (var frame in cancels)
        {
            try
            {
                await WriteFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 对端已结束；cancel 帧尽力而为。
            }
        }

        try
        {
            await _stream.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _ = cancellationToken;
    }

    public async Task<object?> ResultAsync()
    {
        await _done.Task.ConfigureAwait(false);
        if (_error is not null)
        {
            throw _error;
        }
        return _result;
    }

    public void Dispose()
    {
        try
        {
            _callCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _stream.Dispose();
        _callCts.Dispose();
    }

    private void AssertWritable()
    {
        lock (_sync)
        {
            if (_inputClosed || _error is not null || _closed)
            {
                throw new BppException(BppErrorCodes.Cancelled, "interaction 已不能发送");
            }
        }
    }

    private async Task WriteFrameAsync(ClientFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_error is not null || _closed)
                {
                    throw new BppException(BppErrorCodes.Cancelled, "interaction 已关闭");
                }
                _outbound++;
                frame.Header ??= new FrameHeader();
                frame.Header.Sequence = _outbound;
            }
            await _stream.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                if (!await _stream.ResponseStream.MoveNext(_callCts.Token).ConfigureAwait(false))
                {
                    return;
                }
                var frame = _stream.ResponseStream.Current;
                var incoming = frame.Header?.Sequence ?? 0;
                if (incoming != _inbound + 1)
                {
                    Fail(new BppException(
                        BppErrorCodes.ProtocolError,
                        $"PROTOCOL_VIOLATION: sequence 必须从 1 严格递增，收到 {incoming}"));
                    return;
                }
                _inbound = incoming;

                if (frame.Event is not null)
                {
                    await PushAsync(BrickValueCodec.ToClr(frame.Event.Payload)).ConfigureAwait(false);
                    continue;
                }
                if (frame.Response is not null)
                {
                    OnResponse(frame, frame.Response);
                    continue;
                }
                if (frame.Final is not null)
                {
                    lock (_sync)
                    {
                        _peerFinal = true;
                        SettleOpenRequestsLocked();
                    }
                    _result = BrickValueCodec.ToClr(frame.Final.Result);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException error)
        {
            if (_error is null)
            {
                Fail(BrickErrorStatus.ToBppException(error));
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            Finish();
        }
    }

    private void OnResponse(ServerFrame frame, ResponseFrame response)
    {
        var key = frame.Header is null ? string.Empty : Key(frame.Header.ReplyTo.ToByteArray());
        lock (_sync)
        {
            if (!_pending.TryGetValue(key, out var entry) || entry.State != RequestState.Sent)
            {
                return;
            }
            if (response.Error is not null)
            {
                FinishPendingLocked(entry, new BppException(response.Error.Code, response.Error.Message));
                return;
            }
            FinishPendingLocked(entry, null, BrickValueCodec.ToClr(response.Value));
        }
    }

    private async Task PushAsync(object? value)
    {
        try
        {
            await _events.Writer.WriteAsync(value, _callCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private void CancelPending(PendingRequest entry, Exception error)
    {
        var writeCancel = false;
        lock (_sync)
        {
            if (entry.State is RequestState.Settled or RequestState.Cancelled)
            {
                return;
            }
            var previous = entry.State;
            FinishPendingLocked(entry, error);
            writeCancel = previous == RequestState.Sent && !_peerFinal && _error is null && !_inputClosed;
        }

        if (writeCancel)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteFrameAsync(CancelRequestFrame(entry.MessageId), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
        }
    }

    private void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_error is not null)
            {
                return;
            }
            _error = error;
            foreach (var entry in _pending.Values)
            {
                FinishPendingLocked(entry, new BppException(BppErrorCodes.Cancelled, "SESSION_CLOSED: interaction 已取消"));
            }
            _events.Writer.TryComplete();
        }
        try
        {
            _callCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Finish()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            SettleOpenRequestsLocked();
            _events.Writer.TryComplete();
        }
        _done.TrySetResult();
    }

    private void SettleOpenRequestsLocked()
    {
        foreach (var entry in _pending.Values)
        {
            FinishPendingLocked(entry, new BppException(BppErrorCodes.Cancelled, "SESSION_CLOSED: interaction 已取消"));
        }
    }

    private void FinishPendingLocked(PendingRequest entry, Exception? error, object? value = null)
    {
        if (entry.State is RequestState.Settled or RequestState.Cancelled)
        {
            return;
        }
        entry.State = error is null ? RequestState.Settled : RequestState.Cancelled;
        _pending.Remove(Key(entry.MessageId));
        entry.Reply.TrySetResult(new ResponseOutcome(value, error));
    }

    private static ClientFrame CancelRequestFrame(byte[] messageId)
    {
        return new ClientFrame
        {
            Header = new FrameHeader { MessageId = ByteString.CopyFrom(messageId) },
            CancelRequest = new CancelRequestFrame(),
        };
    }

    private static byte[] RandomMessageId()
    {
        var id = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(id);
        return id;
    }

    private static string Key(byte[] messageId) => Convert.ToHexString(messageId);
}
