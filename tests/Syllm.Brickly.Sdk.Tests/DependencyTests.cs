using System.Text.Json;
using Syllm.Brickly.Sdk.Grpc;
using Syllm.Brickly.Sdk.Tests.TestSupport;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class DependencyTests
{
    private const string Bindings =
        """{"openai":{"brickId":"com.brickly.openai","origin":"installed","version":"2.1.0"},"review_tool":{"brickId":"com.brickly.openai","origin":"review","version":"2.1.0"}}""";

    [Fact]
    public async Task DependencyBindingsFromEnvAreReadOnlyAndIsolated()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var bindings = runtime.Dependencies.Bindings;
            Assert.Equal(2, bindings.Count);
            Assert.Equal(BrickOrigin.Installed, bindings["openai"].Origin);
            Assert.Equal(BrickOrigin.Review, bindings["review_tool"].Origin);

            bindings["openai"] = new BrickRef("com.example.other", BrickOrigin.Development, "9.9.9");
            Assert.Equal("com.brickly.openai", runtime.Dependencies.Bindings["openai"].BrickId);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequireRejectsInvalidOrUndeclaredAlias()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var invalid = Assert.Throws<BppException>(() => runtime.Dependencies.Require("BadAlias"));
            Assert.Equal(BppErrorCodes.InvalidInput, invalid.Code);

            var undeclared = Assert.Throws<BppException>(() => runtime.Dependencies.Require("missing"));
            Assert.Equal(BppErrorCodes.DependencyNotDeclared, undeclared.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartOutsideCommandRequiresParent()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var error = await Assert.ThrowsAsync<BppException>(
                () => runtime.Dependencies.Require("openai").StartAsync());
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, error.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartedHandleInvokeInteractDisposeStop()
    {
        var host = await FakeHost.StartAsync();
        host.ConnectorInvokeHandlers[("com.brickly.openai", "chat")] = _ => "chat-ok";
        host.ConnectorInteractFinalResult = () => BrickValueCodec.FromClr("end-ok");
        host.ApplyEnvironment(Bindings);
        var runtime = new BricklyRuntime()
            .OnCommand("dispose-case", async (ctx, _) =>
            {
                var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                var value = await dependency.InvokeAsync("chat", new { prompt = "x" });
                var session = await dependency.InteractAsync(
                    "chat",
                    null,
                    new InteractOptions { OnEvent = _ => { } });
                var final = await session.EndAsync();
                await dependency.DisposeAsync();
                return new Dictionary<string, object?> { ["value"] = value, ["final"] = final };
            })
            .OnCommand("stop-case", async (ctx, _) =>
            {
                var dependency = await ctx.Dependencies().Require("openai").StartAsync();
                await dependency.StopAsync();
                return null;
            });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("dispose-case", null);
            var value = Assert.IsType<Dictionary<string, object?>>(BrickValueCodec.ToClr(result.Result));
            Assert.Equal("chat-ok", value["value"]);
            Assert.Equal("end-ok", value["final"]);
            Assert.Equal("handle-1", host.ConnectorInvokes[0].HandleId);
            Assert.Contains(("handle-1", false), host.DisposedDependencies);

            await client.InvokeAsync("stop-case", null);
            Assert.Contains(("handle-2", true), host.DisposedDependencies);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DependencyInvokeInsideCommandCarriesInvocationId()
    {
        var host = await FakeHost.StartAsync();
        host.ConnectorInvokeHandlers[("com.brickly.openai", "chat")] = _ => "ok";
        host.ApplyEnvironment(Bindings);
        var runtime = new BricklyRuntime().OnCommand("caller", async (ctx, _) =>
        {
            var dependency = ctx.Dependencies().Require("openai");
            return await dependency.InvokeAsync("chat", new { prompt = "hi" });
        });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("caller", null, invocationId: "inv-dep");
            Assert.Contains("inv-dep", host.ConnectorInvokeInvocationIds);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartInCommandSendsInvocationId()
    {
        var host = await FakeHost.StartAsync();
        host.ApplyEnvironment(Bindings);
        var runtime = new BricklyRuntime().OnCommand("starter", async (ctx, _) =>
        {
            var dependency = await ctx.Dependencies().Require("openai").StartAsync();
            await dependency.DisposeAsync();
            return null;
        });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            await client.InvokeAsync("starter", null, invocationId: "inv-start");
            Assert.Contains("inv-start", host.StartedInvocationIds);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task DependencyInteractOutsideCommandRequiresHost()
    {
        FakeHost.ClearEnvironment();
        await using var runtime = new BricklyRuntime();
        runtime.Dependencies.ReplaceFromJson(Bindings);
        var error = await Assert.ThrowsAsync<BppException>(
            () => runtime.Dependencies.Require("openai").InteractAsync(
                "chat",
                null,
                new InteractOptions { OnEvent = _ => { } }));
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
    }

    [Fact]
    public async Task DependencyCallUsesCallIntent()
    {
        var host = await FakeHost.StartAsync();
        host.ConnectorInteractFinalResult = () => BrickValueCodec.FromClr("called");
        host.ApplyEnvironment(Bindings);
        var runtime = new BricklyRuntime().OnCommand("caller", async (ctx, _) =>
        {
            var dependency = ctx.Dependencies().Require("openai");
            return await dependency.CallAsync(
                "chat",
                new { prompt = "x" },
                new CallOptions { OnEvent = _ => { } });
        });
        await runtime.StartAsync();

        try
        {
            using var client = TestHarness.CreateRuntimeClient(host, runtime);
            var result = await client.InvokeAsync("caller", null);
            Assert.Equal("called", BrickValueCodec.ToClr(result.Result));
            Assert.Contains("call", host.ConnectorInteractIntents);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartInEventScopeRejected()
    {
        var (host, runtime) = await TestHarness.StartRuntimeAsync(dependencyBindings: Bindings);
        try
        {
            var captured = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = runtime.Events.On("my-brick:tick", (_, _) =>
            {
                try
                {
                    runtime.Dependencies.Require("openai").StartAsync().GetAwaiter().GetResult();
                    captured.TrySetResult(null);
                }
                catch (Exception error)
                {
                    captured.TrySetResult(error);
                }
            });
            await TestHarness.WaitUntilAsync(() => host.SubscribedTopics.Contains("my-brick:tick"), 15_000);

            host.PushDomainEvent("my-brick:tick", BrickValueCodec.FromClr(new { n = 1 }));
            var error = await captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var bpp = Assert.IsType<BppException>(error);
            Assert.Equal(BppErrorCodes.ParentInvocationRequired, bpp.Code);
        }
        finally
        {
            await runtime.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartInCommandWithoutHostIsProtocol()
    {
        FakeHost.ClearEnvironment();
        await using var runtime = new BricklyRuntime().OnCommand("start-without-host", async (ctx, _) =>
        {
            await ctx.Dependencies().Require("openai").StartAsync();
            return null;
        });
        runtime.Dependencies.ReplaceFromJson(Bindings);
        using var input = JsonDocument.Parse("null");
        var error = await Assert.ThrowsAsync<BppException>(
            () => ((ICommandDispatcher)runtime).DispatchInvokeAsync(
                "start-without-host",
                input.RootElement,
                null,
                CancellationToken.None));
        Assert.Equal(BppErrorCodes.ProtocolError, error.Code);
    }
}
