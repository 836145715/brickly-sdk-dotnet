using System.Text.Json;
using Syllm.Brickly.Sdk.Grpc;

namespace Syllm.Brickly.Sdk;

/// <summary>Host spawn 注入的 Profile 配置快照。</summary>
internal static class ProfileConfig
{
    private static readonly Dictionary<string, object?> Empty = new();

    public static IReadOnlyDictionary<string, object?> Read()
    {
        var raw = Environment.GetEnvironmentVariable(RuntimeMetadata.ProfileConfigEnv);
        if (string.IsNullOrEmpty(raw))
        {
            return Empty;
        }
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }
            var result = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = ToClr(property.Value);
            }
            return result;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string? ReadProfileId() =>
        Environment.GetEnvironmentVariable(RuntimeMetadata.ProfileIdEnv);

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ToClr(property.Value)),
        _ => null,
    };
}
