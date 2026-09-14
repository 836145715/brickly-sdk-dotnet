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
        FakeHost.ClearEnvironment();
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
            Assert.NotNull(host.Register);
            Assert.Equal(1u, host.Register!.Protocol.Major);
            Assert.Equal(0u, host.Register.Protocol.Minor);
            Assert.True(host.Register.Capabilities.SupportsInteract);
            Assert.Contains("hello", host.Register.Capabilities.Commands);
            Assert.Contains("live", host.Register.Capabilities.Commands);
            Assert.False(string.IsNullOrEmpty(host.Register.Endpoint));
            Assert.Equal(host.BootstrapToken, host.RegisterBootstrapToken);
            Assert.Equal(host.RuntimeHandleId, runtime.RuntimeHandleId);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task HostTokenInterceptorRejectsWrongToken()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(r =>
            r.OnCommand("hello", (_, _) => Task.FromResult<object?>(null)));
        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var error = await Assert.ThrowsAsync<RpcException>(
                () => client.InvokeWithWrongTokenAsync("hello"));
            Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task HealthReportsServing()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        try
        {
            var endpoint = host.Register!.Endpoint;
            using var channel = GrpcChannel.ForAddress("http://" + endpoint);
            var client = new Health.HealthClient(channel);
            var metadata = new Metadata { { "x-brickly-host-token", host.HostToRuntimeToken } };
            var response = await client.CheckAsync(
                new HealthCheckRequest { Service = "global::Brickly.Runtime.V1.BrickCommandService" },
                metadata);
            Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, response.Status);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnregisterOnShutdown()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync();
        await runtime.DisposeAsync();
        try
        {
            Assert.True(host.Unregistered);
            Assert.Equal(host.RuntimeToHostToken, host.UnregisterRuntimeToken);
        }
        finally
        {
            await host.DisposeAsync();
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
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("log", null, invocationId: "inv-1");
            await TestHarness.WaitUntilAsync(() => host.PlatformCalls.Any(call => call.Method == "diagnostics.log"));
            var logCall = host.PlatformCalls.First(call => call.Method == "diagnostics.log");
            var payload = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(logCall.Input));
            Assert.Equal("info", payload["level"]);
            Assert.Equal("hello log", payload["message"]);
            Assert.Equal("inv-1", payload["invocationId"]);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
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
            await host.DisposeAsync();
        }
    }
}
