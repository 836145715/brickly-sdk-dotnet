using System.Text;
using System.Text.Json;
using Brickly.Runtime.V1;
using Grpc.Core;
using Syllm.Brickly.Sdk.Grpc;

namespace Syllm.Brickly.Sdk;

/// <summary>
/// 资源读取句柄。按 gRPC 块流式读取，不整份进内存；实现 <see cref="Stream"/>。
/// </summary>
public sealed class ResourceHandle : Stream
{
    /// <summary>整份进内存的上限（200 MiB）；更大请用 Read / SaveToAsync。</summary>
    public const long MaxMaterializationBytes = 200L * 1024 * 1024;

    private readonly HostResourceClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AsyncServerStreamingCall<ResourceChunk>? _call;
    private byte[] _chunk = Array.Empty<byte>();
    private int _chunkOffset;
    private long _position;
    private bool _closed;
    private bool _revoked;
    private bool _eof;

    internal ResourceHandle(HostResourceClient client, ResourceRef reference)
    {
        _client = client;
        Ref = reference;
    }

    /// <summary>资源引用。</summary>
    public ResourceRef Ref { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => Ref.SizeBytes;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>读取整份字节；超过 200 MiB 抛 RESOURCE_MATERIALIZATION_TOO_LARGE。</summary>
    public async Task<byte[]> BytesAsync(CancellationToken cancellationToken = default)
    {
        if (Ref.SizeBytes > MaxMaterializationBytes)
        {
            throw new BppException(
                BppErrorCodes.ResourceMaterializationTooLarge,
                "资源超过 200 MiB，不能整体读取。请使用流读取。");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxMaterializationBytes)
            {
                await CloseAsync().ConfigureAwait(false);
                throw new BppException(
                    BppErrorCodes.ResourceMaterializationTooLarge,
                    "资源整体读取超过 200 MiB。");
            }
        }
        return buffer.ToArray();
    }

    public async Task<string> TextAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await BytesAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>把资源内容解码到 <paramref name="type"/>；type 为 null 时只校验并读取内容。</summary>
    public async Task<object?> JsonAsync(Type? type, CancellationToken cancellationToken = default)
    {
        var bytes = await BytesAsync(cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return null;
        }
        return JsonSerializer.Deserialize(bytes, type);
    }

    public async Task<T?> JsonAsync<T>(CancellationToken cancellationToken = default)
    {
        var bytes = await BytesAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes);
    }

    public async Task SaveToAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new BppException(BppErrorCodes.InvalidInput, "SaveTo destination 不能为空");
        }
        await using var file = File.Create(path);
        await CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_revoked)
            {
                throw new BppException(BppErrorCodes.ResourceExpired, "资源已撤销");
            }
            if (_closed || _eof)
            {
                return 0;
            }
            if (_client is null)
            {
                throw new BppException(BppErrorCodes.ProtocolError, "ResourceService 未就绪");
            }

            EnsureCall();
            while (_chunkOffset >= _chunk.Length)
            {
                if (!await _call!.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    _eof = true;
                    return 0;
                }
                _chunk = _call.ResponseStream.Current.Data.ToByteArray();
                _chunkOffset = 0;
            }

            var count = Math.Min(buffer.Length, _chunk.Length - _chunkOffset);
            _chunk.AsSpan(_chunkOffset, count).CopyTo(buffer.Span);
            _chunkOffset += count;
            _position += count;
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>关闭读取流；幂等。</summary>
    public override void Close()
    {
        _gate.Wait();
        try
        {
            CloseCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>撤销资源（Host 侧不可再用）；先关闭本地流。</summary>
    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        await CloseAsync().ConfigureAwait(false);
        _revoked = true;
        if (_client is null)
        {
            throw new BppException(BppErrorCodes.ProtocolError, "ResourceService 未就绪");
        }
        await _client.RevokeAsync(Ref.ResourceId, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseCore();
            _gate.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void EnsureCall()
    {
        if (_call is not null)
        {
            return;
        }
        _call = _client!.OpenRead(Ref.ResourceId, null, CancellationToken.None);
    }

    private void CloseCore()
    {
        _closed = true;
        _chunk = Array.Empty<byte>();
        _chunkOffset = 0;
        _call?.Dispose();
        _call = null;
    }
}
