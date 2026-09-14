namespace Syllm.Brickly.Sdk;

/// <summary>宿主资源的短期能力引用。</summary>
public sealed record ResourceRef
{
    public const string KindValue = "brickly.resource";

    public string Kind { get; init; } = KindValue;
    public string ResourceId { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? MimeType { get; init; }
    public string? Name { get; init; }

    /// <summary>sha256 十六进制（小写）。</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>过期时间（Unix 毫秒）；0 表示未提供。</summary>
    public long ExpiresAt { get; init; }

    internal bool IsValid()
    {
        return Kind == KindValue &&
               !string.IsNullOrEmpty(ResourceId) &&
               SizeBytes >= 0 &&
               !string.IsNullOrEmpty(Sha256) &&
               ExpiresAt >= 0;
    }

    /// <summary>返回诊断视图。</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var value = new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["resourceId"] = ResourceId,
            ["sizeBytes"] = SizeBytes,
            ["sha256"] = Sha256,
            ["expiresAt"] = ExpiresAt,
        };
        if (MimeType is not null)
        {
            value["mimeType"] = MimeType;
        }
        if (Name is not null)
        {
            value["name"] = Name;
        }
        return value;
    }
}
