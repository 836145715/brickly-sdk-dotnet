using Brickly.Runtime.V1;
using Grpc.Core;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>服务端 interact 帧协议：open / opened / event / request / cancel_request / response / final。</summary>
internal static class InteractServer
{
    public static async Task RunAsync(
        IAsyncStreamReader<ClientFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context,
        ICommandDispatcher dispatcher)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var first = requestStream.Current;
        if (first.Open is null || (first.Header is not null && first.Header.Sequence != 1))
        {
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.Internal, "PROTOCOL_VIOLATION: 首帧必须是 open"));
        }

        var writeGate = new SemaphoreSlim(1, 1);
        ulong sequence = 0;

        async Task WriteAsync(ServerFrame frame)
        {
            await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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

        var session = new InteractServerSession(
            BrickValueCodec.ToJsonElement(first.Open.Input),
            context.CancellationToken,
            WriteAsync)
        {
            InvocationId = context.RequestHeaders.GetValue(RuntimeMetadata.InvocationId),
        };

        var handlerTask = Task.Run(() => dispatcher.DispatchInteractAsync(first.Open.CommandId, session));
        var readerTask = Task.Run(() => ReadLoopAsync(requestStream, session, context.CancellationToken));

        object? result;
        try
        {
            result = await handlerTask.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            session.CloseInput();
            await session.SettleRequestsAsync().ConfigureAwait(false);
            throw BrickErrorStatus.ToRpcException(error);
        }

        session.CloseInput();
        await session.SettleRequestsAsync().ConfigureAwait(false);

        var final = BrickValueCodec.FromClr(result);
        await WriteAsync(new ServerFrame { Final = new FinalFrame { Result = final } }).ConfigureAwait(false);

        _ = readerTask;
    }

    private static async Task ReadLoopAsync(
        IAsyncStreamReader<ClientFrame> requestStream,
        InteractServerSession session,
        CancellationToken cancellationToken)
    {
        ulong inbound = 1;
        try
        {
            while (true)
            {
                if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    session.CloseInput();
                    return;
                }

                var next = requestStream.Current;
                var incoming = next.Header?.Sequence ?? 0;
                if (incoming != inbound + 1)
                {
                    session.CloseInput();
                    return;
                }
                inbound = incoming;

                if (next.Event is not null)
                {
                    await session.PushAsync(BrickValueCodec.ToClr(next.Event.Payload)).ConfigureAwait(false);
                    continue;
                }

                if (next.Request is not null)
                {
                    var messageId = next.Header?.MessageId;
                    if (messageId is null || messageId.Length != 16)
                    {
                        session.CloseInput();
                        return;
                    }
                    session.OnRequest(next.Request.Payload, messageId.ToByteArray());
                    continue;
                }

                if (next.CancelRequest is not null)
                {
                    var messageId = next.Header?.MessageId;
                    if (messageId is not null && messageId.Length == 16)
                    {
                        session.CancelRequest(messageId.ToByteArray());
                    }
                }
            }
        }
        catch (Exception)
        {
            session.CloseInput();
        }
    }
}
