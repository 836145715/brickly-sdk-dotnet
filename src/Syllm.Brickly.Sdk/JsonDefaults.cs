using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syllm.Brickly.Sdk;

/// <summary>SDK 内部 JSON 选项：Host 字段为 camelCase，反序列化忽略大小写。</summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
