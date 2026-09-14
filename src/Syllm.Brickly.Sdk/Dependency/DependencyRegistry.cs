using System.Text.Json;
using System.Text.RegularExpressions;

namespace Syllm.Brickly.Sdk;

/// <summary>保存 Host 注入的不可推断依赖绑定。alias/ref 只读，按完整身份隔离。</summary>
public sealed class DependencyRegistry
{
    private static readonly Regex AliasPattern = new("^[a-z][a-z0-9_-]*$", RegexOptions.Compiled);

    private readonly BricklyRuntime _runtime;
    private readonly object _sync = new();
    private Dictionary<string, BrickRef> _bindings = new();

    internal DependencyRegistry(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>绑定快照；修改返回值不影响内部绑定。</summary>
    public BrickDependencyBindings Bindings
    {
        get
        {
            lock (_sync)
            {
                return new BrickDependencyBindings(_bindings);
            }
        }
    }

    /// <summary>返回绑定到精确 BrickRef 的依赖客户端。</summary>
    public DependencyClient Require(string alias) => RequireScoped(alias, null, null, null);

    internal DependencyClient RequireScoped(
        string alias,
        string? parentRequestId,
        TraceContext? trace,
        IReadOnlyDictionary<string, string>? dependencyProfiles)
    {
        ValidateAlias(alias, BppErrorCodes.InvalidInput);
        BrickRef reference;
        lock (_sync)
        {
            if (!_bindings.TryGetValue(alias, out reference!))
            {
                throw new BppException(
                    BppErrorCodes.DependencyNotDeclared,
                    $"dependency alias {alias} is not declared in the manifest");
            }
        }
        return new DependencyClient(_runtime, alias, reference, parentRequestId, trace, dependencyProfiles);
    }

    /// <summary>从 BRICKLY_DEPENDENCY_BINDINGS 注入（Node / Python 同源行为）。</summary>
    internal void ReplaceFromJson(string raw)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            throw new BppException(BppErrorCodes.ProtocolError, "dependencyBindings must be an object");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BppException(BppErrorCodes.ProtocolError, "dependencyBindings must be an object");
            }

            var next = new Dictionary<string, BrickRef>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                ValidateAlias(property.Name, BppErrorCodes.ProtocolError);
                next[property.Name] = ParseRef(property.Value);
            }

            lock (_sync)
            {
                _bindings = next;
            }
        }
    }

    internal static void ValidateAlias(string alias, string code)
    {
        if (!AliasPattern.IsMatch(alias))
        {
            throw new BppException(code, "dependency alias must match ^[a-z][a-z0-9_-]*$");
        }
    }

    private static BrickRef ParseRef(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new BppException(BppErrorCodes.ProtocolError, "dependencyBindings contains an invalid BrickRef");
        }

        var brickId = ReadString(element, "brickId");
        var version = ReadString(element, "version");
        var originRaw = ReadString(element, "origin");

        if (string.IsNullOrEmpty(brickId) ||
            string.IsNullOrEmpty(version) ||
            !BrickOrigins.TryParse(originRaw, out var origin))
        {
            throw new BppException(BppErrorCodes.ProtocolError, "dependencyBindings contains an invalid BrickRef");
        }

        return new BrickRef(brickId, origin, version);
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }
}

/// <summary>绑定当前 command 的 parent、trace 与 Profile 映射。</summary>
public sealed class ScopedDependencyRegistry
{
    private readonly DependencyRegistry _registry;
    private readonly string? _parentRequestId;
    private readonly TraceContext? _trace;
    private readonly IReadOnlyDictionary<string, string>? _dependencyProfiles;

    internal ScopedDependencyRegistry(
        DependencyRegistry registry,
        string? parentRequestId,
        TraceContext? trace,
        IReadOnlyDictionary<string, string>? dependencyProfiles)
    {
        _registry = registry;
        _parentRequestId = parentRequestId;
        _trace = trace;
        _dependencyProfiles = dependencyProfiles;
    }

    /// <summary>返回当前 command 作用域的依赖客户端。</summary>
    public DependencyClient Require(string alias) =>
        _registry.RequireScoped(alias, _parentRequestId, _trace, _dependencyProfiles);
}
