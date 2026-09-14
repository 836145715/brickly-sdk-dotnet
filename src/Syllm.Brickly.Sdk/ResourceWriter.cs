using System.Text;
using Syllm.Brickly.Sdk.Grpc;

namespace Syllm.Brickly.Sdk;

/// <summary>
/// 资源上传 Writer：把任意大小的 write 聚合成连续 1 MiB wire 分块。
/// FinishAsync / Abort 幂等；关闭后再写返回 RESOURCE_UPLOAD_CLOSED。
/// </summary>
public sealed class ResourceWriter
{
    private const int ChunkBytes = RuntimeMetadata.ResourceChunkBytes;
    private const int StringChunkChars = ChunkBytes / 4;

    private enum WriterState
    {
        Open,
        Finished,
        Aborted,
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ResourceCreateStream _upload;
    private readonly HostResourceClient _client;
    private byte[] _pending = Array.Empty<byte>();
    private WriterState _state = WriterState.Open;
    private ResourceHandle? _handle;

    internal ResourceWriter(ResourceCreateStream upload, HostResourceClient client)
    {
        _upload = upload;
        _client = client;
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != WriterState.Open)
            {
                throw new BppException(BppErrorCodes.ResourceUploadClosed, "资源上传已结束");
            }
            if (data.IsEmpty)
            {
                return;
            }

            var offset = 0;
            if (_pending.Length > 0)
            {
                var need = ChunkBytes - _pending.Length;
                var take = Math.Min(data.Length, need);
                _pending = Concat(_pending, data.Slice(0, take));
                offset = take;
                if (_pending.Length >= ChunkBytes)
                {
                    await FlushAsync(ChunkBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            while (offset + ChunkBytes <= data.Length)
            {
                await _upload.SendChunkAsync(data.Slice(offset, ChunkBytes).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                offset += ChunkBytes;
            }

            if (offset < data.Length)
            {
                _pending = Concat(_pending, data[offset..]);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> WriteStringAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var written = 0;
        var chars = value.AsMemory();
        while (!chars.IsEmpty)
        {
            var take = Math.Min(chars.Length, StringChunkChars);
            if (take < chars.Length && char.IsHighSurrogate(chars.Span[take - 1]))
            {
                take--;
            }
            var bytes = Encoding.UTF8.GetBytes(chars.Span[..take].ToString());
            await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            written += bytes.Length;
            chars = chars[take..];
        }
        return written;
    }

    public async Task<long> ReadFromAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                await WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
            if (read == 0)
            {
                return total;
            }
        }
    }

    public async Task<ResourceHandle> FinishAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == WriterState.Finished && _handle is not null)
            {
                return _handle;
            }
            if (_state != WriterState.Open)
            {
                throw new BppException(BppErrorCodes.ResourceUploadClosed, "资源上传已结束");
            }
            if (_pending.Length > 0)
            {
                await FlushAsync(_pending.Length, cancellationToken).ConfigureAwait(false);
            }
            var proto = await _upload.FinishAsync(cancellationToken).ConfigureAwait(false);
            _handle = new ResourceHandle(_client, BrickValueCodec.ToSdkResourceRef(proto));
            _state = WriterState.Finished;
            return _handle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Abort()
    {
        _gate.Wait();
        try
        {
            if (_state != WriterState.Open)
            {
                return;
            }
            _state = WriterState.Aborted;
            _pending = Array.Empty<byte>();
            _upload.Abort();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushAsync(int size, CancellationToken cancellationToken)
    {
        var chunk = _pending.AsSpan(0, size).ToArray();
        _pending = _pending.AsSpan(size).ToArray();
        await _upload.SendChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Concat(byte[] left, ReadOnlyMemory<byte> right)
    {
        if (left.Length == 0)
        {
            return right.ToArray();
        }
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0);
        right.CopyTo(result.AsMemory(left.Length));
        return result;
    }
}
