using System.Security.Cryptography;
using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>测试装配：FakeHost + Runtime + Runtime 客户端。</summary>
public static class TestHarness
{
    public static async Task<(FakeHost Host, BricklyRuntime Runtime)> StartRuntimeAsync(
        Action<BricklyRuntime>? configure = null,
        string? dependencyBindings = null,
        string? profileConfig = null)
    {
        var host = await FakeHost.StartAsync().ConfigureAwait(false);
        host.ApplyEnvironment(dependencyBindings, profileConfig);
        var runtime = new BricklyRuntime();
        configure?.Invoke(runtime);
        await runtime.StartAsync().ConfigureAwait(false);
        return (host, runtime);
    }

    public static RuntimeClient CreateRuntimeClient(FakeHost host, BricklyRuntime runtime)
    {
        var endpoint = host.Register?.Endpoint
            ?? throw new InvalidOperationException("Runtime 尚未注册");
        return new RuntimeClient(endpoint, host.HostToRuntimeToken);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}

/// <summary>以 Host 身份调用 Runtime 的 BrickCommandService 客户端。</summary>
public sealed class RuntimeClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly BrickCommandService.BrickCommandServiceClient _client;
    private readonly Metadata _metadata;

    public RuntimeClient(string endpoint, string hostToken)
    {
        _channel = GrpcChannel.ForAddress("http://" + endpoint, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024,
        });
        _client = new BrickCommandService.BrickCommandServiceClient(_channel);
        _metadata = new Metadata { { "x-brickly-host-token", hostToken } };
    }

    public Task<InvokeResult> InvokeAsync(string commandId, object? input, string? invocationId = null)
    {
        var metadata = new Metadata();
        foreach (var entry in _metadata)
        {
            metadata.Add(entry);
        }
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add("x-brickly-invocation-id", invocationId);
        }
        return _client
            .InvokeAsync(new InvokeRequest
            {
                CommandId = commandId,
                Input = BrickValueCodec.FromClr(input),
            }, metadata)
            .ResponseAsync;
    }

    public Task<InvokeResult> InvokeWithWrongTokenAsync(string commandId)
    {
        var metadata = new Metadata { { "x-brickly-host-token", "wrong-token" } };
        return _client
            .InvokeAsync(new InvokeRequest { CommandId = commandId }, metadata)
            .ResponseAsync;
    }

    public AsyncDuplexStreamingCall<ClientFrame, ServerFrame> RawInteract() => _client.Interact(_metadata);

    public async Task<ClientInteractSession> OpenInteractAsync(
        string commandId,
        object? input,
        string? invocationId = null)
    {
        var metadata = new Metadata();
        foreach (var entry in _metadata)
        {
            metadata.Add(entry);
        }
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add("x-brickly-invocation-id", invocationId);
        }
        var call = _client.Interact(metadata);
        var session = new ClientInteractSession(call);
        await session.WriteAsync(new ClientFrame
        {
            Open = new OpenFrame { CommandId = commandId, Input = BrickValueCodec.FromClr(input) },
        }).ConfigureAwait(false);
        var first = await session.ReadAsync().ConfigureAwait(false);
        if (first.Opened is null)
        {
            throw new InvalidOperationException("服务端首帧必须是 opened");
        }
        return session;
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>Host 侧 interact 客户端会话。</summary>
public sealed class ClientInteractSession
{
    private readonly AsyncDuplexStreamingCall<ClientFrame, ServerFrame> _call;
    private ulong _outbound;

    public ClientInteractSession(AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call)
    {
        _call = call;
    }

    public Task WriteAsync(ClientFrame frame)
    {
        frame.Header ??= new FrameHeader();
        frame.Header.Sequence = ++_outbound;
        return _call.RequestStream.WriteAsync(frame);
    }

    /// <summary>原样写入（用于构造 sequence 违规等异常帧）。</summary>
    public Task WriteRawAsync(ClientFrame frame) => _call.RequestStream.WriteAsync(frame);

    public async Task<ServerFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("interact 流已结束");
        }
        return _call.ResponseStream.Current;
    }

    public Task SendEventAsync(object? payload) =>
        WriteAsync(new ClientFrame { Event = new EventFrame { Payload = BrickValueCodec.FromClr(payload) } });

    public async Task<object?> RequestAsync(object? payload, CancellationToken cancellationToken = default)
    {
        var messageId = RandomMessageId();
        await WriteAsync(new ClientFrame
        {
            Header = new FrameHeader { MessageId = ByteString.CopyFrom(messageId) },
            Request = new RequestFrame { Payload = BrickValueCodec.FromClr(payload) },
        }).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Response is not null &&
                frame.Header is not null &&
                frame.Header.ReplyTo.ToByteArray().AsSpan().SequenceEqual(messageId))
            {
                if (frame.Response.Error is not null)
                {
                    throw new BppException(frame.Response.Error.Code, frame.Response.Error.Message);
                }
                return BrickValueCodec.ToClr(frame.Response.Value);
            }
        }
    }

    public Task CloseInputAsync() => _call.RequestStream.CompleteAsync();

    public async Task<ServerFrame> WaitForFinalAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Final is not null)
            {
                return frame;
            }
        }
    }

    public void Dispose() => _call.Dispose();

    private static byte[] RandomMessageId()
    {
        var id = new byte[16];
        RandomNumberGenerator.Fill(id);
        return id;
    }
}
