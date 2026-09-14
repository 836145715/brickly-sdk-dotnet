using Brickly.Runtime.V1;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Runtime → Host ResourceService 客户端。</summary>
internal sealed class HostResourceClient : IDisposable
{
    private readonly GrpcChannel? _channel;
    private readonly ResourceService.ResourceServiceClient _client;
    private readonly string _token;

    public HostResourceClient(string endpoint, string runtimeToHostToken)
    {
        _channel = ChannelFactory.Create(endpoint, RuntimeMetadata.ResourceMaxBytes);
        _client = new ResourceService.ResourceServiceClient(_channel);
        _token = runtimeToHostToken;
    }

    public HostResourceClient(ResourceService.ResourceServiceClient client, string token)
    {
        _client = client;
        _token = token;
    }

    public ResourceCreateStream BeginCreate(ResourceCreateHeader header, Metadata? extraMetadata, CancellationToken cancellationToken)
    {
        var call = _client.Create(Auth(extraMetadata), cancellationToken: cancellationToken);
        return new ResourceCreateStream(call, header);
    }

    public AsyncServerStreamingCall<ResourceChunk> OpenRead(string resourceId, Metadata? extraMetadata, CancellationToken cancellationToken)
    {
        var request = new ResourceReadRequest { ResourceId = resourceId };
        return _client.Read(request, Auth(extraMetadata), cancellationToken: cancellationToken);
    }

    public async Task<byte[]> ReadAllAsync(string resourceId, Metadata? extraMetadata, CancellationToken cancellationToken)
    {
        using var call = OpenRead(resourceId, extraMetadata, cancellationToken);
        using var buffer = new MemoryStream();
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            buffer.Write(call.ResponseStream.Current.Data.Span);
        }
        return buffer.ToArray();
    }

    public async Task<ResourceMetadata> StatAsync(string resourceId, CancellationToken cancellationToken)
    {
        var request = new ResourceStatRequest { ResourceId = resourceId };
        return await _client.StatAsync(request, Auth(null), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(string resourceId, CancellationToken cancellationToken)
    {
        var request = new ResourceRevokeRequest { ResourceId = resourceId };
        await _client.RevokeAsync(request, Auth(null), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _channel?.Dispose();

    private Metadata Auth(Metadata? extra)
    {
        var metadata = new Metadata { { RuntimeMetadata.RuntimeToken, _token } };
        if (extra is not null)
        {
            foreach (var entry in extra)
            {
                metadata.Add(entry);
            }
        }
        return metadata;
    }
}

/// <summary>客户端流式 Create 会话。</summary>
internal sealed class ResourceCreateStream
{
    private readonly AsyncClientStreamingCall<ResourceWriteFrame, global::Brickly.Runtime.V1.ResourceRef> _call;
    private ulong _offset;
    private bool _headerSent;

    public ResourceCreateStream(
        AsyncClientStreamingCall<ResourceWriteFrame, global::Brickly.Runtime.V1.ResourceRef> call,
        ResourceCreateHeader header)
    {
        _call = call;
        Header = header;
    }

    public ResourceCreateHeader Header { get; }

    public async Task SendChunkAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (!_headerSent)
        {
            await _call.RequestStream.WriteAsync(new ResourceWriteFrame { Header = Header }, cancellationToken)
                .ConfigureAwait(false);
            _headerSent = true;
        }
        if (data.Length == 0)
        {
            return;
        }
        var frame = new ResourceWriteFrame
        {
            Chunk = new ResourceWriteChunk
            {
                Offset = _offset,
                Data = ByteString.CopyFrom(data),
            },
        };
        await _call.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        _offset += (ulong)data.Length;
    }

    public async Task<global::Brickly.Runtime.V1.ResourceRef> FinishAsync(CancellationToken cancellationToken)
    {
        if (!_headerSent)
        {
            await _call.RequestStream.WriteAsync(new ResourceWriteFrame { Header = Header }, cancellationToken)
                .ConfigureAwait(false);
            _headerSent = true;
        }
        await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        return await _call.ResponseAsync.ConfigureAwait(false);
    }

    public void Abort() => _call.Dispose();
}
