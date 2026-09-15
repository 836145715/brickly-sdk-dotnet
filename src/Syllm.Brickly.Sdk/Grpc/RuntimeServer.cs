using System.Net;
using System.Security.Cryptography;
using System.Text;
using Brickly.Runtime.V1;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Runtime 内嵌 gRPC server 的启动参数。</summary>
internal sealed class RuntimeServerOptions
{
    public required string HostToRuntimeToken { get; init; }
    public required ICommandDispatcher Dispatcher { get; init; }
}

/// <summary>内嵌 Kestrel gRPC server：loopback HTTP/2 + host token 鉴权 + 健康检查。</summary>
internal sealed class RuntimeServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RuntimeServer(WebApplication app, string endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>注册给 Host 的 loopback endpoint（127.0.0.1:port）。</summary>
    public string Endpoint { get; }

    public static async Task<RuntimeServer> StartAsync(RuntimeServerOptions options, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "Syllm.Brickly.Sdk",
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<HostTokenInterceptor>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = RuntimeMetadata.InvokeMaxBytes;
            grpc.MaxSendMessageSize = RuntimeMetadata.InvokeMaxBytes;
            grpc.Interceptors.Add<HostTokenInterceptor>();
        });
        builder.Services.AddSingleton<BrickCommandServiceImpl>();

        var app = builder.Build();
        app.MapGrpcService<BrickCommandServiceImpl>();
        app.MapGrpcService<RuntimeHealthService>();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new BppException(BppErrorCodes.ProtocolError, "Runtime server 未暴露 loopback endpoint");
        var uri = new Uri(address);
        return new RuntimeServer(app, $"{uri.Host}:{uri.Port}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭路径失败不掩盖主错误。
        }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>校验 Host → Runtime 的 host token。</summary>
internal sealed class HostTokenInterceptor : Interceptor
{
    private readonly RuntimeServerOptions _options;

    public HostTokenInterceptor(RuntimeServerOptions options)
    {
        _options = options;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        await continuation(requestStream, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var value = context.RequestHeaders.GetValue(RuntimeMetadata.HostToken);
        if (value is null || !FixedTimeEquals(value, _options.HostToRuntimeToken))
        {
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.Unauthenticated, "host token 无效"));
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>标准 gRPC 健康检查：BrickCommandService 报 SERVING。</summary>
internal sealed class RuntimeHealthService : global::Grpc.Health.V1.Health.HealthBase
{
    public override Task<global::Grpc.Health.V1.HealthCheckResponse> Check(
        global::Grpc.Health.V1.HealthCheckRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new global::Grpc.Health.V1.HealthCheckResponse
        {
            Status = global::Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving,
        });
    }

    public override async Task Watch(
        global::Grpc.Health.V1.HealthCheckRequest request,
        IServerStreamWriter<global::Grpc.Health.V1.HealthCheckResponse> responseStream,
        ServerCallContext context)
    {
        await responseStream.WriteAsync(new global::Grpc.Health.V1.HealthCheckResponse
        {
            Status = global::Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus.Serving,
        }).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>BrickCommandService 服务端实现。</summary>
internal sealed class BrickCommandServiceImpl : BrickCommandService.BrickCommandServiceBase
{
    private readonly RuntimeServerOptions _options;

    public BrickCommandServiceImpl(RuntimeServerOptions options)
    {
        _options = options;
    }

    public override async Task<InvokeResult> Invoke(InvokeRequest request, ServerCallContext context)
    {
        try
        {
            var invocationId = context.RequestHeaders.GetValue(RuntimeMetadata.InvocationId);
            var result = await _options.Dispatcher
                .DispatchInvokeAsync(
                    request.CommandId,
                    BrickValueCodec.ToJsonElement(request.Input),
                    invocationId,
                    context.CancellationToken)
                .ConfigureAwait(false);
            return new InvokeResult { Result = BrickValueCodec.FromClr(result) };
        }
        catch (Exception error)
        {
            throw BrickErrorStatus.ToRpcException(error);
        }
    }

    public override async Task Interact(
        IAsyncStreamReader<ClientFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context) =>
        await InteractServer
            .RunAsync(requestStream, responseStream, context, _options.Dispatcher)
            .ConfigureAwait(false);
}
