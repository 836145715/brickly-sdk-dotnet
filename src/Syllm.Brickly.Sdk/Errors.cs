namespace Syllm.Brickly.Sdk;

/// <summary>
/// SDK 统一错误类型，等价于 Node SDK 的 BppError。
/// <see cref="Code"/> 对应宿主统一错误码；命令 handler 抛出时 SDK 会保留 code 回传宿主。
/// </summary>
public class BppException : Exception
{
    public BppException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public BppException(string code, string message, Exception innerException, object? details = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    /// <summary>宿主统一错误码。</summary>
    public string Code { get; }

    /// <summary>可选错误细节（不进入线上协议）。</summary>
    public object? Details { get; }
}

/// <summary>SDK 内常用错误码常量。</summary>
public static class BppErrorCodes
{
    public const string ParentInvocationRequired = "PARENT_INVOCATION_REQUIRED";
    public const string ProtocolError = "PROTOCOL_ERROR";
    public const string InvalidInput = "INVALID_INPUT";
    public const string CommandNotFound = "COMMAND_NOT_FOUND";
    public const string Cancelled = "CANCELLED";
    public const string Internal = "INTERNAL";
    public const string ResourceUploadClosed = "RESOURCE_UPLOAD_CLOSED";
    public const string ResourceQuotaExceeded = "RESOURCE_QUOTA_EXCEEDED";
    public const string ResourceMaterializationTooLarge = "RESOURCE_MATERIALIZATION_TOO_LARGE";
    public const string InvalidResourceRef = "INVALID_RESOURCE_REF";
    public const string ResourceExpired = "RESOURCE_EXPIRED";
    public const string DependencyNotDeclared = "DEPENDENCY_NOT_DECLARED";
    public const string HandleClosed = "HANDLE_CLOSED";
    public const string RuntimeUnavailable = "RUNTIME_UNAVAILABLE";
}
