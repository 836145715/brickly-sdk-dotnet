namespace Syllm.Brickly.Sdk;

/// <summary>创建资源的可选参数。</summary>
public sealed class ResourceCreateOptions
{
    /// <summary>MIME 类型；为空时 string 默认 text/plain; charset=utf-8，byte[] 默认 application/octet-stream。</summary>
    public string? MimeType { get; init; }

    /// <summary>展示用文件名。</summary>
    public string? Name { get; init; }

    /// <summary>资源 TTL（毫秒）。</summary>
    public long TtlMillis { get; init; }

    /// <summary>预期大小（字节）；流式上传时可选。</summary>
    public long ExpectedSizeBytes { get; init; }
}
