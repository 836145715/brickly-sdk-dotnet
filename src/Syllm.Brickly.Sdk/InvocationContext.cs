namespace Syllm.Brickly.Sdk;

/// <summary>跨进程追踪上下文。</summary>
public sealed class TraceContext
{
    public string TraceId { get; init; } = string.Empty;
    public string? ParentSpanId { get; init; }
    public string? Generation { get; init; }
}

/// <summary>宿主注入的可信 command 调用来源。</summary>
public sealed class CommandInvocationContext
{
    /// <summary>调用来源；未提供时为 "unknown"。</summary>
    public string Source { get; init; } = "unknown";

    public string? TriggerId { get; init; }
    public string? HotkeyId { get; init; }
    public object? Binding { get; init; }
    public string? ProfileId { get; init; }

    /// <summary>按 alias 对应的精确 BrickKey 选择 Profile。</summary>
    public IReadOnlyDictionary<string, string>? DependencyProfiles { get; init; }
}
