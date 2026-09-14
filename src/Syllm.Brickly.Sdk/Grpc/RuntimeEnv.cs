namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Host spawn Runtime 时注入的启动环境。</summary>
internal sealed record StartEnvironment(
    string HostEndpoint,
    string BootstrapToken,
    string RuntimeToHostToken,
    string HostToRuntimeToken);

internal static class RuntimeEnv
{
    /// <summary>读取并清除一次性启动环境；缺任意一项返回 null。</summary>
    public static StartEnvironment? Take()
    {
        var endpoint = Environment.GetEnvironmentVariable(RuntimeMetadata.HostEndpointEnv);
        var bootstrap = Environment.GetEnvironmentVariable(RuntimeMetadata.BootstrapTokenEnv);
        var runtimeToHost = Environment.GetEnvironmentVariable(RuntimeMetadata.RuntimeToHostTokenEnv);
        var hostToRuntime = Environment.GetEnvironmentVariable(RuntimeMetadata.HostToRuntimeTokenEnv);

        if (string.IsNullOrEmpty(endpoint) ||
            string.IsNullOrEmpty(bootstrap) ||
            string.IsNullOrEmpty(runtimeToHost) ||
            string.IsNullOrEmpty(hostToRuntime))
        {
            return null;
        }

        Environment.SetEnvironmentVariable(RuntimeMetadata.BootstrapTokenEnv, null);
        Environment.SetEnvironmentVariable(RuntimeMetadata.RuntimeToHostTokenEnv, null);
        Environment.SetEnvironmentVariable(RuntimeMetadata.HostToRuntimeTokenEnv, null);

        return new StartEnvironment(endpoint, bootstrap, runtimeToHost, hostToRuntime);
    }

    /// <summary>把 host:port 规范成 GrpcChannel 可用的 http URI。</summary>
    public static string ToHttpAddress(string endpoint)
    {
        if (endpoint.Contains("://", StringComparison.Ordinal))
        {
            return endpoint;
        }
        return "http://" + endpoint;
    }
}
