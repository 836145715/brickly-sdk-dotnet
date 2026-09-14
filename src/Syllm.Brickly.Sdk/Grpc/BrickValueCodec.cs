using System.Buffers;
using System.Collections;
using System.Text.Json;
using Brickly.Runtime.V1;
using Google.Protobuf;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>
/// BrickValue 与 CLR / JSON 的双向转换。硬限额与 specs/runtime/v1/common.proto 一致：
/// 深度 64、10 万节点、安全整数 ±(2^53-1)、number 必须有限且非负零。
/// </summary>
internal static class BrickValueCodec
{
    public const int MaxDepth = 64;
    public const int MaxNodes = 100_000;
    private const long MaxSafeInteger = (1L << 53) - 1;

    public static BrickValue FromClr(object? value)
    {
        var counter = new NodeCounter();
        return FromClr(value, 0, counter);
    }

    private static BrickValue FromClr(object? value, int depth, NodeCounter counter)
    {
        if (depth > MaxDepth)
        {
            throw new BppException("INVALID_PAYLOAD", "payload 嵌套层级过深");
        }
        counter.Add();

        switch (value)
        {
            case null:
                return NullValue();
            case BrickValue brickValue:
                return brickValue;
            case ResourceHandle handle:
                return FromResourceRef(handle.Ref);
            case ResourceRef reference:
                return FromResourceRef(reference);
            case JsonElement element:
                return FromJson(element, depth, counter);
            case JsonDocument document:
                return FromJson(document.RootElement, depth, counter);
            case string text:
                return new BrickValue { StringValue = text };
            case bool boolean:
                return new BrickValue { BoolValue = boolean };
            case byte[] bytes:
                return new BrickValue { BytesValue = ByteString.CopyFrom(bytes) };
            case sbyte or byte or short or ushort or int or uint or long:
                return IntegerValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            case ulong unsigned:
                if (unsigned > MaxSafeInteger)
                {
                    throw new BppException("INVALID_PAYLOAD", "无符号整数超出 BrickValue 可编码范围");
                }
                return IntegerValue((long)unsigned);
            case float single:
                return NumberValue(single);
            case double number:
                return NumberValue(number);
            case decimal decimalValue:
                if (decimal.Truncate(decimalValue) == decimalValue &&
                    decimalValue >= -MaxSafeInteger && decimalValue <= MaxSafeInteger)
                {
                    return IntegerValue((long)decimalValue);
                }
                return NumberValue((double)decimalValue);
            case IReadOnlyDictionary<string, object?> readOnlyMap:
                return FromMap(readOnlyMap, depth, counter);
            case IDictionary dictionary:
                return FromNonGenericMap(dictionary, depth, counter);
            case IEnumerable enumerable:
                return FromEnumerable(enumerable, depth, counter);
            default:
                return FromUnknownObject(value, depth, counter);
        }
    }

    private static BrickValue FromUnknownObject(object value, int depth, NodeCounter counter)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(value);
            return FromJson(element, depth, counter);
        }
        catch (NotSupportedException exception)
        {
            throw new BppException("INVALID_PAYLOAD", $"无法编码的类型：{value.GetType()}", exception);
        }
        catch (JsonException exception)
        {
            throw new BppException("INVALID_PAYLOAD", $"无法编码的类型：{value.GetType()}", exception);
        }
    }

    private static BrickValue FromMap(IReadOnlyDictionary<string, object?> map, int depth, NodeCounter counter)
    {
        var fields = new List<BrickObjectField>(map.Count);
        foreach (var pair in map)
        {
            fields.Add(new BrickObjectField
            {
                Key = pair.Key,
                Value = FromClr(pair.Value, depth + 1, counter),
            });
        }
        return new BrickValue { ObjectValue = new BrickObject { Fields = { fields } } };
    }

    private static BrickValue FromNonGenericMap(IDictionary map, int depth, NodeCounter counter)
    {
        var fields = new List<BrickObjectField>(map.Count);
        foreach (DictionaryEntry entry in map)
        {
            if (entry.Key is not string key)
            {
                throw new BppException("INVALID_PAYLOAD", "资源 payload 的 map key 必须是字符串");
            }
            fields.Add(new BrickObjectField
            {
                Key = key,
                Value = FromClr(entry.Value, depth + 1, counter),
            });
        }
        return new BrickValue { ObjectValue = new BrickObject { Fields = { fields } } };
    }

    private static BrickValue FromEnumerable(IEnumerable enumerable, int depth, NodeCounter counter)
    {
        var items = new List<BrickValue>();
        foreach (var item in enumerable)
        {
            items.Add(FromClr(item, depth + 1, counter));
        }
        return new BrickValue { ListValue = new BrickList { Items = { items } } };
    }

    public static BrickValue FromJson(JsonElement element)
    {
        var counter = new NodeCounter();
        return FromJson(element, 0, counter);
    }

    private static BrickValue FromJson(JsonElement element, int depth, NodeCounter counter)
    {
        if (depth > MaxDepth)
        {
            throw new BppException("INVALID_PAYLOAD", "payload 嵌套层级过深");
        }
        counter.Add();

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return NullValue();
            case JsonValueKind.True:
                return new BrickValue { BoolValue = true };
            case JsonValueKind.False:
                return new BrickValue { BoolValue = false };
            case JsonValueKind.String:
                return new BrickValue { StringValue = element.GetString() ?? string.Empty };
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer) && integer >= -MaxSafeInteger && integer <= MaxSafeInteger)
                {
                    return IntegerValue(integer);
                }
                return NumberValue(element.GetDouble());
            case JsonValueKind.Array:
            {
                var items = new List<BrickValue>();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FromJson(item, depth + 1, counter));
                }
                return new BrickValue { ListValue = new BrickList { Items = { items } } };
            }
            case JsonValueKind.Object:
            {
                var fields = new List<BrickObjectField>();
                foreach (var property in element.EnumerateObject())
                {
                    fields.Add(new BrickObjectField
                    {
                        Key = property.Name,
                        Value = FromJson(property.Value, depth + 1, counter),
                    });
                }
                return new BrickValue { ObjectValue = new BrickObject { Fields = { fields } } };
            }
            default:
                return NullValue();
        }
    }

    public static BrickValue NullValue() =>
        new() { NullValue = new global::Brickly.Runtime.V1.NullValue() };

    private static BrickValue IntegerValue(long value)
    {
        if (value < -MaxSafeInteger || value > MaxSafeInteger)
        {
            throw new BppException("INVALID_PAYLOAD", "BrickValue.integer 超出安全整数范围");
        }
        return new BrickValue { SafeIntegerValue = value };
    }

    private static BrickValue NumberValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || (value == 0 && double.IsNegative(value)))
        {
            throw new BppException("INVALID_PAYLOAD", "BrickValue.number 必须是有限非负零数字");
        }
        return new BrickValue { NumberValue = value };
    }

    /// <summary>BrickValue → CLR 值；resources 非空时把 ResourceRef 水合成 ResourceHandle。</summary>
    public static object? ToClr(BrickValue? value, HostResourceClient? resources = null, int depth = 0)
    {
        if (value is null || depth > MaxDepth)
        {
            return null;
        }

        switch (value.ValueCase)
        {
            case BrickValue.ValueOneofCase.NullValue:
            case BrickValue.ValueOneofCase.None:
                return null;
            case BrickValue.ValueOneofCase.BoolValue:
                return value.BoolValue;
            case BrickValue.ValueOneofCase.SafeIntegerValue:
                return value.SafeIntegerValue;
            case BrickValue.ValueOneofCase.NumberValue:
                return value.NumberValue;
            case BrickValue.ValueOneofCase.StringValue:
                return value.StringValue;
            case BrickValue.ValueOneofCase.BytesValue:
                return value.BytesValue.ToByteArray();
            case BrickValue.ValueOneofCase.ListValue:
            {
                var items = new List<object?>(value.ListValue.Items.Count);
                foreach (var item in value.ListValue.Items)
                {
                    items.Add(ToClr(item, resources, depth + 1));
                }
                return items;
            }
            case BrickValue.ValueOneofCase.ObjectValue:
            {
                var map = new Dictionary<string, object?>(value.ObjectValue.Fields.Count);
                foreach (var field in value.ObjectValue.Fields)
                {
                    map[field.Key] = ToClr(field.Value, resources, depth + 1);
                }
                return map;
            }
            case BrickValue.ValueOneofCase.ResourceValue:
            {
                var reference = ToSdkResourceRef(value.ResourceValue);
                return resources is null ? reference.ToDictionary() : new ResourceHandle(resources, reference);
            }
            default:
                return null;
        }
    }

    /// <summary>BrickValue → JsonElement（命令 handler 输入、诊断视图）。</summary>
    public static JsonElement ToJsonElement(BrickValue? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteJson(writer, value, 0);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteJson(Utf8JsonWriter writer, BrickValue? value, int depth)
    {
        if (value is null || depth > MaxDepth)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.ValueCase)
        {
            case BrickValue.ValueOneofCase.NullValue:
            case BrickValue.ValueOneofCase.None:
                writer.WriteNullValue();
                break;
            case BrickValue.ValueOneofCase.BoolValue:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case BrickValue.ValueOneofCase.SafeIntegerValue:
                writer.WriteNumberValue(value.SafeIntegerValue);
                break;
            case BrickValue.ValueOneofCase.NumberValue:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case BrickValue.ValueOneofCase.StringValue:
                writer.WriteStringValue(value.StringValue);
                break;
            case BrickValue.ValueOneofCase.BytesValue:
                writer.WriteBase64StringValue(value.BytesValue.Span);
                break;
            case BrickValue.ValueOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (var item in value.ListValue.Items)
                {
                    WriteJson(writer, item, depth + 1);
                }
                writer.WriteEndArray();
                break;
            case BrickValue.ValueOneofCase.ObjectValue:
                writer.WriteStartObject();
                foreach (var field in value.ObjectValue.Fields)
                {
                    writer.WritePropertyName(field.Key);
                    WriteJson(writer, field.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case BrickValue.ValueOneofCase.ResourceValue:
                WriteResourceJson(writer, value.ResourceValue);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteResourceJson(Utf8JsonWriter writer, global::Brickly.Runtime.V1.ResourceRef reference)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", ResourceRef.KindValue);
        writer.WriteString("resourceId", reference.ResourceId);
        writer.WriteNumber("sizeBytes", (long)reference.SizeBytes);
        writer.WriteString("sha256", Convert.ToHexString(reference.Sha256.ToByteArray()).ToLowerInvariant());
        var expiresAt = reference.ExpiresAt is null ? 0 : reference.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds();
        writer.WriteNumber("expiresAt", expiresAt);
        if (!string.IsNullOrEmpty(reference.MediaType))
        {
            writer.WriteString("mimeType", reference.MediaType);
        }
        if (!string.IsNullOrEmpty(reference.Name))
        {
            writer.WriteString("name", reference.Name);
        }
        writer.WriteEndObject();
    }

    public static global::Brickly.Runtime.V1.ResourceRef ToProtoResourceRef(ResourceRef reference)
    {
        var proto = new global::Brickly.Runtime.V1.ResourceRef
        {
            ResourceId = reference.ResourceId,
            SizeBytes = (ulong)reference.SizeBytes,
            Sha256 = ByteString.CopyFrom(ParseHex(reference.Sha256)),
        };
        if (reference.MimeType is not null)
        {
            proto.MediaType = reference.MimeType;
        }
        if (reference.Name is not null)
        {
            proto.Name = reference.Name;
        }
        if (reference.ExpiresAt > 0)
        {
            proto.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(reference.ExpiresAt));
        }
        return proto;
    }

    public static ResourceRef ToSdkResourceRef(global::Brickly.Runtime.V1.ResourceRef proto)
    {
        var expiresAt = proto.ExpiresAt is null
            ? 0
            : proto.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds();
        return new ResourceRef
        {
            Kind = ResourceRef.KindValue,
            ResourceId = proto.ResourceId,
            SizeBytes = (long)proto.SizeBytes,
            MimeType = string.IsNullOrEmpty(proto.MediaType) ? null : proto.MediaType,
            Name = string.IsNullOrEmpty(proto.Name) ? null : proto.Name,
            Sha256 = Convert.ToHexString(proto.Sha256.ToByteArray()).ToLowerInvariant(),
            ExpiresAt = expiresAt,
        };
    }

    private static BrickValue FromResourceRef(ResourceRef reference)
    {
        if (!reference.IsValid())
        {
            throw new BppException(BppErrorCodes.InvalidResourceRef, "ResourceRef 格式无效");
        }
        return new BrickValue { ResourceValue = ToProtoResourceRef(reference) };
    }

    private static byte[] ParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return Array.Empty<byte>();
        }
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private sealed class NodeCounter
    {
        private int _count;

        public void Add()
        {
            _count++;
            if (_count > MaxNodes)
            {
                throw new BppException("INVALID_PAYLOAD", "payload 节点数超出 BrickValue 限额");
            }
        }
    }
}
