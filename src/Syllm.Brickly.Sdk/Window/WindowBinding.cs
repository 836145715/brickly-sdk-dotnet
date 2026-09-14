namespace Syllm.Brickly.Sdk;

/// <summary>窗口 binding 规范化；语义与 Go SDK window_binding.go 对齐。</summary>
internal static class WindowBinding
{
    private const string LifetimeRemoved = "已删除 options.lifetime，请用 ctx.UI（Call）或 Runtime.UI（Session）";
    private const string CallKeepAliveForbidden = "ctx.UI 不能 keepAlive，请使用 Runtime.UI";
    private const string CallBindingOnly = "ctx.UI 只能创建 Call 窗口";
    private const string SessionBindingOnly = "Runtime.UI 只能创建 Session 窗口";
    private const string KeepAliveMustShow = "keepAlive 窗口创建时必须展示";
    private const string KeepAliveMismatch = "keepAlive 与 binding 不一致";

    /// <summary>ctx.UI 使用：stamps binding=call。</summary>
    public static WindowOptions NormalizeCall(WindowOptions? options)
    {
        var cloned = Clone(options);
        RejectLifetime(cloned);
        if (cloned.ContainsKey("keepAlive"))
        {
            throw new BppException(BppErrorCodes.InvalidInput, CallKeepAliveForbidden);
        }
        if (BindingKind(cloned) is { } kind && kind != "call")
        {
            throw new BppException(BppErrorCodes.InvalidInput, CallBindingOnly);
        }
        cloned.Remove("lifetime");
        cloned["binding"] = new Dictionary<string, object?> { ["kind"] = "call" };
        cloned["show"] = ShownOf(options);
        return cloned;
    }

    /// <summary>Runtime.UI 使用：stamps binding=session。</summary>
    public static WindowOptions NormalizeSession(WindowOptions? options)
    {
        var cloned = Clone(options);
        RejectLifetime(cloned);
        var shown = ShownOf(options);
        var keepAlive = false;
        if (BindingKind(cloned) is { } kind && kind != "session")
        {
            throw new BppException(BppErrorCodes.InvalidInput, SessionBindingOnly);
        }
        if (BindingKeepAlive(cloned) is { } nested)
        {
            keepAlive = nested;
        }
        if (cloned.TryGetValue("keepAlive", out var top))
        {
            var topKeepAlive = top is bool flag && flag;
            if (BindingKeepAlive(cloned) is { } nestedValue && nestedValue != topKeepAlive)
            {
                throw new BppException(BppErrorCodes.InvalidInput, KeepAliveMismatch);
            }
            keepAlive = topKeepAlive;
        }
        if (keepAlive && !shown)
        {
            throw new BppException(BppErrorCodes.InvalidInput, KeepAliveMustShow);
        }
        cloned.Remove("lifetime");
        cloned.Remove("keepAlive");
        cloned["binding"] = new Dictionary<string, object?> { ["kind"] = "session", ["keepAlive"] = keepAlive };
        cloned["show"] = shown;
        return cloned;
    }

    private static WindowOptions Clone(WindowOptions? options)
    {
        return options is null ? new WindowOptions() : new WindowOptions(options);
    }

    private static void RejectLifetime(WindowOptions options)
    {
        if (options.ContainsKey("lifetime"))
        {
            throw new BppException(BppErrorCodes.InvalidInput, LifetimeRemoved);
        }
    }

    private static bool ShownOf(WindowOptions? options)
    {
        if (options is not null && options.TryGetValue("show", out var show) && show is bool flag)
        {
            return flag;
        }
        return true;
    }

    private static string? BindingKind(WindowOptions options)
    {
        if (!options.TryGetValue("binding", out var raw) || raw is not IReadOnlyDictionary<string, object?> binding)
        {
            return null;
        }
        return binding.TryGetValue("kind", out var kind) ? kind as string : null;
    }

    private static bool? BindingKeepAlive(WindowOptions options)
    {
        if (!options.TryGetValue("binding", out var raw) || raw is not IReadOnlyDictionary<string, object?> binding)
        {
            return null;
        }
        if (!binding.TryGetValue("keepAlive", out var value))
        {
            return null;
        }
        return value is bool flag && flag;
    }
}
