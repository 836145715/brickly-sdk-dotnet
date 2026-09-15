using Brickly.Runtime.V1;
using Syllm.Brickly.Sdk.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>测试装配：真 test-host（子进程）+ Runtime + Runtime 裸客户端。</summary>
public static class TestHarness
{
    /// <summary>
    /// 起真 test-host → /spawn 铸凭据 → 注入环境变量 → 启动 Runtime。
    /// 宿主侧断言经 /calls 录制与驱动面端点完成（不再读假宿主内存态）。
    /// </summary>
    public static async Task<(TestHostProcess Host, BricklyRuntime Runtime)> StartRuntimeAsync(
        Action<BricklyRuntime>? configure = null,
        string? dependencyBindings = null,
        string? profileConfig = null,
        string brickId = "com.brickly.test-app")
    {
        var host = TestHostProcess.TryStart()
            ?? throw new InvalidOperationException("真宿主测试工件不可用（需要 node + @syllm/brickly-test-host）");
        try
        {
            await host.SpawnAsync(brickId).ConfigureAwait(false);
            host.ApplyEnvironment(dependencyBindings: dependencyBindings, profileConfig: profileConfig);
            var runtime = new BricklyRuntime();
            configure?.Invoke(runtime);
            await runtime.StartAsync().ConfigureAwait(false);
            return (host, runtime);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 直连 runtime 的裸客户端（错 token / trailer 断言用；非假宿主——
    /// 只是个 BrickCommandService client，正常驱动请走 host.InvokeAsync）。
    /// </summary>
    public static async Task<RuntimeClient> CreateRuntimeClientAsync(TestHostProcess host)
    {
        var endpoint = await host.RuntimeEndpointAsync().ConfigureAwait(false);
        var token = host.LastSpawn?.HostToRuntimeToken
            ?? throw new InvalidOperationException("尚无 spawn 凭据");
        return new RuntimeClient(endpoint, token);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>异步版等待（谓词需要查控制面时用）。</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}

/// <summary>以 Host 身份直连 Runtime 的 BrickCommandService 裸客户端。</summary>
public sealed class RuntimeClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly BrickCommandService.BrickCommandServiceClient _client;
    private readonly Metadata _metadata;

    public RuntimeClient(string endpoint, string hostToken)
    {
        _channel = GrpcChannel.ForAddress("http://" + endpoint, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024,
        });
        _client = new BrickCommandService.BrickCommandServiceClient(_channel);
        _metadata = new Metadata { { "x-brickly-host-token", hostToken } };
    }

    public Task<InvokeResult> InvokeAsync(string commandId, object? input, string? invocationId = null)
    {
        var metadata = new Metadata();
        foreach (var entry in _metadata)
        {
            metadata.Add(entry);
        }
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add("x-brickly-invocation-id", invocationId);
        }
        return _client
            .InvokeAsync(new InvokeRequest
            {
                CommandId = commandId,
                Input = BrickValueCodec.FromClr(input),
            }, metadata)
            .ResponseAsync;
    }

    public Task<InvokeResult> InvokeWithWrongTokenAsync(string commandId)
    {
        var metadata = new Metadata { { "x-brickly-host-token", "wrong-token" } };
        return _client
            .InvokeAsync(new InvokeRequest { CommandId = commandId }, metadata)
            .ResponseAsync;
    }

    public void Dispose() => _channel.Dispose();
}
