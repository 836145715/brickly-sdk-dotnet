using System.Text;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class ResourceTests
{
    private const string ResourcePrefix = "/ResourceService/";

    [Fact]
    public async Task OpenResourceIsLazy()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var handle = runtime.OpenResource(ValidRef("res_lazy", 3));
            Assert.Equal("res_lazy", handle.Ref.ResourceId);
            var calls = await host.CallsAsync();
            Assert.DoesNotContain(calls, call => call.Path.Contains(ResourcePrefix, StringComparison.Ordinal));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task OpenResourceRejectsInvalidRef()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var error = Assert.Throws<BppException>(() => runtime.OpenResource(new ResourceRef()));
            Assert.Equal(BppErrorCodes.InvalidResourceRef, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CreateResourceEncodesTextBytesMetadata()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var handle = await runtime.CreateResourceAsync(
                "hello",
                new ResourceCreateOptions { Name = "note.txt" });
            Assert.Equal(5, handle.Ref.SizeBytes);
            Assert.Equal("note.txt", handle.Ref.Name);
            Assert.Equal("text/plain; charset=utf-8", handle.Ref.MimeType);
            Assert.False(string.IsNullOrEmpty(handle.Ref.Sha256));

            // 真宿主校验：写入的数据经真实 blob store 可读回（比读假宿主内存字典更强）
            Assert.Equal("hello", await handle.TextAsync());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CreateResourceLargeContentUsesWriter()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var data = Encoding.UTF8.GetBytes(new string('a', 1_500_000));
            var handle = await runtime.CreateResourceAsync(data);
            Assert.Equal(data.Length, handle.Ref.SizeBytes);
            Assert.Equal(data, await handle.BytesAsync());

            // Create 是 client-streaming：frames 记录 header+chunk 帧数（替代 CreateChunkCounts）
            var create = await host.WaitCallAsync(call =>
                call.Path.EndsWith("ResourceService/Create", StringComparison.Ordinal));
            Assert.True(create.Frames >= 2, $"1.5MB 上传应分多帧，实际 {create.Frames}");
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CreateFromStreamsAndAbortsOnFailure()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            using var source = new ThrowingStream(1024);
            await Assert.ThrowsAsync<IOException>(() => runtime.CreateResourceFromAsync(source));
            await Task.Delay(150);

            // 中止发生在首帧写出前（header 懒发送）：宿主侧要么看不到 Create
            // 调用（流未发起），要么流已开但零消息——两种时序下都不应有任何帧到达
            var calls = await host.CallsAsync();
            var creates = calls.Where(call =>
                call.Path.EndsWith("ResourceService/Create", StringComparison.Ordinal)).ToList();
            Assert.All(creates, call => Assert.Equal(0, call.Frames));
            var after = await runtime.CreateResourceAsync("after-abort");
            Assert.Equal("after-abort", await after.TextAsync());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CreateFromStreamsLargeSource()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var data = new byte[1_200_000];
            Random.Shared.NextBytes(data);
            using var source = new MemoryStream(data);
            var handle = await runtime.CreateResourceFromAsync(source);
            Assert.Equal(data.Length, handle.Ref.SizeBytes);
            Assert.Equal(data, await handle.BytesAsync());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task WriterAggregatesAndFinishAbortIdempotent()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var writer = await runtime.CreateResourceWriterAsync();
            await writer.WriteAsync(new byte[100]);
            await writer.WriteAsync(new byte[1024 * 1024]);
            await writer.WriteAsync(new byte[100]);
            var handle = await writer.FinishAsync();
            var again = await writer.FinishAsync();
            Assert.Same(handle, again);
            Assert.Equal(100 + 1024 * 1024 + 100, handle.Ref.SizeBytes);

            var error = await Assert.ThrowsAsync<BppException>(() => writer.WriteAsync(new byte[1]));
            Assert.Equal(BppErrorCodes.ResourceUploadClosed, error.Code);
            writer.Abort();
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task WriterAbortRejectsFurtherWrites()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var writer = await runtime.CreateResourceWriterAsync();
            await writer.WriteAsync(new byte[10]);
            writer.Abort();
            var error = await Assert.ThrowsAsync<BppException>(() => writer.WriteAsync(new byte[1]));
            Assert.Equal(BppErrorCodes.ResourceUploadClosed, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task CommandScopedWriterCarriesInvocationId()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("make", async (ctx, _) =>
            {
                var writer = await ctx.CreateResourceWriterAsync();
                await writer.WriteAsync(new byte[10]);
                var handle = await writer.FinishAsync();
                return handle.Ref.ResourceId;
            }));
        try
        {
            using var client = await TestHarness.CreateRuntimeClientAsync(host);
            var result = await client.InvokeAsync("make", null, invocationId: "inv-writer");
            Assert.IsType<string>(BrickValueCodec.ToClr(result.Result));
            var create = await host.WaitCallAsync(call =>
                call.Path.EndsWith("ResourceService/Create", StringComparison.Ordinal));
            Assert.Equal("inv-writer", create.InvocationId);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task MaterializationLimitRejectsOver200MiB()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var handle = runtime.OpenResource(ValidRef("res_big", 300L * 1024 * 1024));
            var error = await Assert.ThrowsAsync<BppException>(() => handle.BytesAsync());
            Assert.Equal(BppErrorCodes.ResourceMaterializationTooLarge, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ResourceHandleStreamsChunks()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var data = new byte[2_500_000];
            Random.Shared.NextBytes(data);
            var handle = await runtime.CreateResourceAsync(data);
            using var buffer = new MemoryStream();
            var chunk = new byte[128 * 1024];
            while (true)
            {
                var read = await handle.ReadAsync(chunk);
                if (read == 0)
                {
                    break;
                }
                buffer.Write(chunk, 0, read);
            }
            Assert.Equal(data, buffer.ToArray());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ResourceHandleRevokeCallsHost()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var handle = await runtime.CreateResourceAsync("revoke-me");
            await handle.RevokeAsync();
            var revoke = await host.WaitCallAsync(call =>
                call.Path.EndsWith("ResourceService/Revoke", StringComparison.Ordinal));
            Assert.Equal(
                handle.Ref.ResourceId,
                revoke.Request.GetProperty("resourceId").GetString());
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    private static ResourceRef ValidRef(string id, long size) => new()
    {
        ResourceId = id,
        SizeBytes = size,
        Sha256 = new string('a', 64),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
    };

    private sealed class ThrowingStream : Stream
    {
        private readonly int _failAfter;
        private int _read;

        public ThrowingStream(int failAfter)
        {
            _failAfter = failAfter;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _failAfter)
            {
                throw new IOException("source failed");
            }
            var size = Math.Min(count, _failAfter - _read);
            Array.Fill(buffer, (byte)1, offset, size);
            _read += size;
            return size;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _failAfter)
            {
                throw new IOException("source failed");
            }
            var size = Math.Min(buffer.Length, _failAfter - _read);
            buffer.Span[..size].Fill(1);
            _read += size;
            return ValueTask.FromResult(size);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
