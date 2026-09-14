using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>出站 gRPC channel 工厂：loopback 明文 HTTP/2 + 12 MiB 信封上限。</summary>
internal static class ChannelFactory
{
    public static GrpcChannel Create(string endpoint, int maxBytes)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
        };
        return GrpcChannel.ForAddress(RuntimeEnv.ToHttpAddress(endpoint), new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = maxBytes,
            MaxSendMessageSize = maxBytes,
        });
    }
}
