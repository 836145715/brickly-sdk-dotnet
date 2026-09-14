namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>线上 metadata key 与 Host 注入环境变量名。与 Node / Go / Python 保持一致。</summary>
internal static class RuntimeMetadata
{
    public const string BootstrapToken = "x-brickly-bootstrap-token";
    public const string RuntimeToken = "x-brickly-runtime-token";
    public const string HostToken = "x-brickly-host-token";
    public const string InvocationId = "x-brickly-invocation-id";
    public const string TargetBrickId = "x-brickly-target-brick-id";
    public const string HandleId = "x-brickly-handle-id";
    public const string Intent = "x-brickly-intent";

    public const string HostEndpointEnv = "BRICKLY_HOST_ENDPOINT";
    public const string BootstrapTokenEnv = "BRICKLY_BOOTSTRAP_TOKEN";
    public const string RuntimeToHostTokenEnv = "BRICKLY_RUNTIME_TO_HOST_TOKEN";
    public const string HostToRuntimeTokenEnv = "BRICKLY_HOST_TO_RUNTIME_TOKEN";
    public const string DependencyBindingsEnv = "BRICKLY_DEPENDENCY_BINDINGS";
    public const string ProfileIdEnv = "BRICKLY_PROFILE_ID";
    public const string ProfileConfigEnv = "BRICKLY_PROFILE_CONFIG";

    /// <summary>与宿主 10 MiB 业务顶对齐，12 MiB 留给 protobuf 信封。</summary>
    public const int InvokeMaxBytes = 12 * 1024 * 1024;

    /// <summary>资源流单条消息上限。</summary>
    public const int ResourceMaxBytes = 4 * 1024 * 1024;

    /// <summary>资源上传 wire 分块。</summary>
    public const int ResourceChunkBytes = 1024 * 1024;
}
