using System.Text.Json;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>
/// /calls 录制的请求体解码器：把 ts-proto 解码形状（BrickValue 为扁平可选字段
/// 对象，bytes 已归一化为 {$bytes:base64}）还原为 CLR 值，供录制断言使用。
/// </summary>
public static class WireValue
{
    /// <summary>解码 BrickValue JSON 形状；非 BrickValue 对象按字段递归展开。</summary>
    public static object? ToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ToClr).ToList();
        }
        return DecodeObject(element);
    }

    /// <summary>录制请求体的 input/payload 字段（BrickValue）→ CLR。</summary>
    public static object? InputOf(JsonElement request)
    {
        return request.TryGetProperty("input", out var input) ? ToClr(input) : null;
    }

    /// <summary>录制请求体的 payload 字段（Publish 等）→ CLR。</summary>
    public static object? PayloadOf(JsonElement request)
    {
        return request.TryGetProperty("payload", out var payload) ? ToClr(payload) : null;
    }

    private static object? DecodeObject(JsonElement element)
    {
        // BrickValue：oneof 扁平字段，命中任一即按 BrickValue 解码
        if (element.TryGetProperty("nullValue", out _)) return null;
        if (element.TryGetProperty("boolValue", out var b)) return b.GetBoolean();
        if (element.TryGetProperty("safeIntegerValue", out var i)) return i.GetInt64();
        if (element.TryGetProperty("numberValue", out var n)) return n.GetDouble();
        if (element.TryGetProperty("stringValue", out var s)) return s.GetString();
        if (element.TryGetProperty("bytesValue", out var bytes)) return DecodeBytes(bytes);
        if (element.TryGetProperty("listValue", out var list))
        {
            var items = list.TryGetProperty("items", out var array)
                ? array.EnumerateArray().Select(ToClr).ToList()
                : [];
            return items;
        }
        if (element.TryGetProperty("objectValue", out var obj))
        {
            var map = new Dictionary<string, object?>();
            if (obj.TryGetProperty("fields", out var fields))
            {
                foreach (var field in fields.EnumerateArray())
                {
                    var key = field.TryGetProperty("key", out var k) ? k.GetString() : null;
                    if (key is null) continue;
                    map[key] = field.TryGetProperty("value", out var v) ? ToClr(v) : null;
                }
            }
            return map;
        }
        // {$bytes: base64} 标记（normalizeWire 产物）
        if (element.TryGetProperty("$bytes", out var packed) && packed.ValueKind == JsonValueKind.String)
        {
            return Convert.FromBase64String(packed.GetString()!);
        }
        // 普通对象（非 BrickValue）：逐字段展开
        var plain = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            plain[property.Name] = ToClr(property.Value);
        }
        return plain;
    }

    private static object DecodeBytes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("$bytes", out var packed))
        {
            return Convert.FromBase64String(packed.GetString()!);
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return Convert.FromBase64String(element.GetString()!);
        }
        return ToClr(element)!;
    }
}
