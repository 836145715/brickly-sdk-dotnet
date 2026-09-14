using System.Threading.Channels;
using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Google.Protobuf;
using Grpc.Core;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>测试侧 interact 对端会话：可配置 request 应答，并允许主动推送事件/请求/final。</summary>
public sealed class FakeInteractSession
{
    private readonly Channel<ServerFrame> _outbound = Channel.CreateUnbounded<ServerFrame>();

    public List<ClientFrame> Inbound { get; } = new();

    public List<EventFrame> Events { get; } = new();

    public List<RequestFrame> Requests { get; } = new();

    public List<CancelRequestFrame> Cancels { get; } = new();

    public List<OpenFrame> Opens { get; } = new();

    public Func<BrickValue, Task<BrickValue>>? RequestHandler { get; set; }

    public Func<BrickValue, Task>? EventHandler { get; set; }

    /// <summary>调用方 closeInput 后自动回 final；null 表示不回。</summary>
    public Func<BrickValue>? FinalResult { get; set; }

    public bool FinalSent { get; private set; }

    internal ChannelReader<ServerFrame> Outbound => _outbound.Reader;

    public Task SendEventAsync(BrickValue payload) =>
        _outbound.Writer
            .WriteAsync(new ServerFrame { Event = new EventFrame { Payload = payload } })
            .AsTask();

    public Task SendFinalAsync(BrickValue result)
    {
        FinalSent = true;
        return _outbound.Writer
            .WriteAsync(new ServerFrame { Final = new FinalFrame { Result = result } })
            .AsTask();
    }

    internal void CloseInput() => _outbound.Writer.TryComplete();
}

public static class FakeInteractSessionRunner
{
    public static async Task RunAsync(
        IAsyncStreamReader<ClientFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context,
        FakeInteractSession session)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var open = requestStream.Current;
        if (open.Open is null)
        {
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.Internal, "首帧必须是 open"));
        }
        session.Opens.Add(open.Open);

        ulong sequence = 0;
        var writeGate = new SemaphoreSlim(1, 1);
        async Task WriteAsync(ServerFrame frame)
        {
            await writeGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                sequence++;
                frame.Header ??= new FrameHeader();
                frame.Header.Sequence = sequence;
                await responseStream.WriteAsync(frame).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        await WriteAsync(new ServerFrame { Opened = new OpenedFrame() }).ConfigureAwait(false);

        var writerTask = Task.Run(async () =>
        {
            try
            {
                while (await session.Outbound.WaitToReadAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    while (session.Outbound.TryRead(out var frame))
                    {
                        await WriteAsync(frame).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                // 流结束。
            }
        });

        ulong inbound = 1;
        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                var incoming = frame.Header?.Sequence ?? 0;
                if (incoming != inbound + 1)
                {
                    return;
                }
                inbound = incoming;
                session.Inbound.Add(frame);

                if (frame.Event is not null)
                {
                    session.Events.Add(frame.Event);
                    if (session.EventHandler is not null)
                    {
                        await session.EventHandler(frame.Event.Payload).ConfigureAwait(false);
                    }
                    continue;
                }

                if (frame.Request is not null)
                {
                    session.Requests.Add(frame.Request);
                    var messageId = frame.Header?.MessageId.ToByteArray() ?? Array.Empty<byte>();
                    var requestPayload = frame.Request.Payload;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = session.RequestHandler is not null
                                ? await session.RequestHandler(requestPayload).ConfigureAwait(false)
                                : BrickValueCodec.NullValue();
                            await WriteAsync(new ServerFrame
                            {
                                Header = new FrameHeader { ReplyTo = ByteString.CopyFrom(messageId) },
                                Response = new ResponseFrame { Value = result },
                            }).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 测试会话允许请求无应答。
                        }
                    });
                    continue;
                }

                if (frame.CancelRequest is not null)
                {
                    session.Cancels.Add(frame.CancelRequest);
                }
            }
        }
        catch (Exception)
        {
            // 流结束。
        }
        finally
        {
            if (session.FinalResult is not null)
            {
                try
                {
                    await WriteAsync(new ServerFrame
                    {
                        Final = new FinalFrame { Result = session.FinalResult() },
                    }).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 流已关闭。
                }
            }
            session.CloseInput();
        }

        _ = writerTask;
    }
}
