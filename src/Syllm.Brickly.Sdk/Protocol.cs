namespace Syllm.Brickly.Sdk;

/// <summary>SDK 协议与版本常量。</summary>
public static class Protocol
{
    /// <summary>生产协议包名（gRPC Register 使用 major=1, minor=0）。</summary>
    public const string ProtocolVersion = "brickly.runtime.v1";

    /// <summary>SDK 发布版本（与 Node / Go / Python 包版本对齐）。</summary>
    public const string SdkVersion = "0.11.0";
}
