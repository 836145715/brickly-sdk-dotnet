using System.Text.Json;

namespace Syllm.Brickly.Sdk;

/// <summary>Brick 制品来源。</summary>
public enum BrickOrigin
{
    Installed,
    Development,
    Review,
}

/// <summary>BrickOrigin 与线上字符串的转换。</summary>
public static class BrickOrigins
{
    public const string InstalledWire = "installed";
    public const string DevelopmentWire = "development";
    public const string ReviewWire = "review";

    public static string ToWire(BrickOrigin origin) => origin switch
    {
        BrickOrigin.Installed => InstalledWire,
        BrickOrigin.Development => DevelopmentWire,
        BrickOrigin.Review => ReviewWire,
        _ => throw new BppException(BppErrorCodes.InvalidInput, "BrickRef.origin is invalid"),
    };

    public static bool TryParse(string value, out BrickOrigin origin)
    {
        switch (value)
        {
            case InstalledWire:
                origin = BrickOrigin.Installed;
                return true;
            case DevelopmentWire:
                origin = BrickOrigin.Development;
                return true;
            case ReviewWire:
                origin = BrickOrigin.Review;
                return true;
            default:
                origin = default;
                return false;
        }
    }
}

/// <summary>跨来源、跨版本调用的完整目标身份。</summary>
public sealed record BrickRef(string BrickId, BrickOrigin Origin, string Version)
{
    /// <summary>与 Host 一致的 BrickKey 标量编码（JSON 数组）。</summary>
    public string ToBrickKey() => BrickKeyOf(this);

    /// <summary>与 Host 一致的 BrickKey 标量编码（JSON 数组）。</summary>
    public static string BrickKeyOf(BrickRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return JsonSerializer.Serialize(new[] { BrickOrigins.ToWire(reference.Origin), reference.BrickId, reference.Version });
    }
}

/// <summary>Host 注入的 alias → 精确目标身份映射。</summary>
public sealed class BrickDependencyBindings : Dictionary<string, BrickRef>
{
    public BrickDependencyBindings()
    {
    }

    public BrickDependencyBindings(IDictionary<string, BrickRef> source)
        : base(source)
    {
    }

    /// <summary>返回浅拷贝快照。</summary>
    public BrickDependencyBindings Snapshot() => new(this);
}
