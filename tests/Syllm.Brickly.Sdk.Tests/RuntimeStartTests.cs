using Grpc.Core;
using Grpc.Health.V1;
using Syllm.Brickly.Sdk.Grpc;
using Grpc.Net.Client;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class RuntimeStartTests
{
    [Fact]
    public async Task StartRejectsMissingHostEndpoint()
    {
        TestHostProcess.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        var error = await Assert.ThrowsAsync<BppException>(() => runtime.StartAsync());
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
        Assert.Contains("BRICKLY_HOST_ENDPOINT", error.Message);
    }

    [Fact]
    public async Task RegisterDeclaresProtocolCommandsAndInteract()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("hello", (_, _) => Task.FromResult<object?>(new { ok = true }))
                .OnCommand("live", (_, _) => Task.FromResult<object?>(null)));
        try
        {
            // 真宿主校验 bootstrap token（spawn-registry byBootstrap）：
            // 能注册成功即证明 token 正确，无需再读 metadata 断言。
            var register = await host.WaitCallAsync(
                call => call.Path.EndsWith("RuntimeRegistry/Register", StringComparison.Ordinal));
            var request = register.Request;
            Assert.Equal(1, request.GetProperty("protocol").GetProperty("major").GetInt32());
            Assert.Equal(0, request.GetProperty("protocol").GetProperty("minor").GetInt32());
            var capabilities = request.GetProperty("capabilities");
            Assert.True(capabilities.GetProperty("supportsInteract").GetBoolean());
            var commands = capabilities.GetProperty("commands")
                .EnumerateArray().Select(item => item.GetString()).ToList();
            Assert.Contains("hello", commands);
            Assert.Contains("live", commands);
            Assert.False(string.IsNullOrEmpty(request.GetProperty("endpoint").GetString()));
            Assert.False(string.IsNullOrEmpty(runtime.RuntimeHandleId));
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HostTokenInterceptorRejectsWrongToken()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("hello", (_, _) => Task.FromResult<object?>(null)));
        try
        {
            using var client = await TestHarness.CreateRuntimeClientAsync(host);
            var error = await Assert.ThrowsAsync<RpcException>(
                () => client.InvokeWithWrongTokenAsync("hello"));
            Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task HealthReportsServing()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var endpoint = await host.RuntimeEndpointAsync();
            using var channel = GrpcChannel.ForAddress("http://" + endpoint);
            var client = new Health.HealthClient(channel);
            var metadata = new Metadata
            {
                { "x-brickly-host-token", host.LastSpawn!.HostToRuntimeToken },
            };
            var response = await client.CheckAsync(
                new HealthCheckRequest { Service = "global::Brickly.Runtime.V1.BrickCommandService" },
                metadata);
            Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, response.Status);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task UnregisterOnShutdown()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        await runtime.DisposeAsync();
        try
        {
            var unregister = await host.WaitCallAsync(
                call => call.Path.EndsWith("RuntimeRegistry/Unregister", StringComparison.Ordinal));
            Assert.NotNull(unregister);
            // 真宿主语义：unregister 后 handle 从 /runtimes 消失
            await TestHarness.WaitUntilAsync(async () =>
            {
                var runtimes = await host.RuntimesAsync();
                return !runtimes.GetProperty("runtimes").EnumerateArray().Any(item =>
                    item.GetProperty("spawnId").GetString() == host.LastSpawn!.SpawnId);
            });
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task DiagnosticsLogCarriesInvocationId()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("log", (ctx, _) =>
            {
                ctx.Info("hello log", new Dictionary<string, object?> { ["n"] = 1 });
                return Task.FromResult<object?>(null);
            }));
        try
        {
            using var client = await TestHarness.CreateRuntimeClientAsync(host);
            await client.InvokeAsync("log", null, invocationId: "inv-1");
            var logCall = await host.WaitCallAsync(call =>
                call.Path.EndsWith("PlatformService/Call", StringComparison.Ordinal) &&
                call.Request.TryGetProperty("method", out var method) &&
                method.GetString() == "diagnostics.log");
            var payload = Assert.IsType<Dictionary<string, object?>>(
                WireValue.InputOf(logCall.Request));
            Assert.Equal("info", payload["level"]);
            Assert.Equal("hello log", payload["message"]);
            Assert.Equal("inv-1", payload["invocationId"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ShutdownHookRunsOnDispose()
    {
        var shutdownCalled = false;
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnShutdown(() =>
            {
                shutdownCalled = true;
                return Task.CompletedTask;
            }));
        await runtime.DisposeAsync();
        try
        {
            Assert.True(shutdownCalled);
        }
        finally
        {
            host.Dispose();
        }
    }
}
