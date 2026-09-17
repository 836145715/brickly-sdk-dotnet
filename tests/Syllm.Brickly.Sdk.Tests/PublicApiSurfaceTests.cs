using System.Reflection;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class PublicApiSurfaceTests
{
    [Fact]
    public void PublicApiSurfaceExposesRuntimeApis()
    {
        Assert.Equal("brickly.runtime.v1", Protocol.ProtocolVersion);
        Assert.Equal("0.12.0", Protocol.SdkVersion);

        var runtimeType = typeof(BricklyRuntime);
        foreach (var property in new[] { "UI", "Events", "Platform", "System", "Dependencies", "Storage", "Config" })
        {
            Assert.NotNull(runtimeType.GetProperty(property));
        }
        foreach (var method in new[]
        {
            "OnCommand",
            "OnReady",
            "OnShutdown",
            "StartAsync",
            "WaitForShutdownAsync",
            "InvokeAsync",
            "InteractAsync",
            "CallAsync",
            "OpenResource",
            "CreateResourceAsync",
            "CreateResourceFromAsync",
            "CreateResourceWriterAsync",
            "Debug",
            "Info",
            "Warn",
            "Error",
        })
        {
            Assert.NotNull(runtimeType.GetMethod(method));
        }

        Assert.NotNull(typeof(CommandContext).GetProperty("RequestID"));
        Assert.NotNull(typeof(CommandContext).GetProperty("CommandID"));
        Assert.NotNull(typeof(CommandContext).GetProperty("Invocation"));
        Assert.NotNull(typeof(CommandContext).GetProperty("CancellationToken"));
        Assert.NotNull(typeof(CommandContext).GetMethod("SendAsync"));
        Assert.NotNull(typeof(CommandContext).GetMethod("OnEvent"));
        Assert.NotNull(typeof(CommandContext).GetMethod("HandleRequests"));
        Assert.NotNull(typeof(CommandContext).GetProperty("Closed"));
        Assert.NotNull(typeof(DependencyClient).GetMethod("InvokeAsync"));
        Assert.NotNull(typeof(DependencyClient).GetMethod("StartAsync"));
        Assert.NotNull(typeof(ResourceHandle).GetMethod("BytesAsync"));
        Assert.NotNull(typeof(ResourceWriter).GetMethod("FinishAsync"));
        Assert.NotNull(typeof(WindowHandle).GetMethod("CloseAsync"));
        Assert.NotNull(typeof(EventBus).GetMethod("On"));
        Assert.NotNull(typeof(EventBus).GetMethod("PublishAsync"));
    }

    [Fact]
    public void PublicApiSurfaceDoesNotLeakGeneratedTypes()
    {
        var assembly = typeof(BricklyRuntime).Assembly;
        var exported = assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("Syllm.Brickly.Sdk", StringComparison.Ordinal) == true);

        foreach (var type in exported)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.False(IsGenerated(method.ReturnType), $"{type.FullName}.{method.Name} 返回了生成类型");
                foreach (var parameter in method.GetParameters())
                {
                    Assert.False(IsGenerated(parameter.ParameterType), $"{type.FullName}.{method.Name} 参数泄漏生成类型");
                }
            }
            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.False(IsGenerated(property.PropertyType), $"{type.FullName}.{property.Name} 泄漏生成类型");
            }
        }
    }

    [Fact]
    public void PublicApiSurfaceHasNoRefFirstAliases()
    {
        var runtimeType = typeof(BricklyRuntime);
        Assert.Null(runtimeType.GetMethod("InvokeSelf"));
        Assert.Null(runtimeType.GetMethod("InvokeRoot"));
        Assert.Null(typeof(DependencyClient).GetMethod("InvokeRoot"));
    }

    private static bool IsGenerated(Type type)
    {
        if (type.Namespace?.StartsWith("Brickly.Runtime.V1", StringComparison.Ordinal) == true)
        {
            return true;
        }
        if (type.IsArray)
        {
            return IsGenerated(type.GetElementType()!);
        }
        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsGenerated);
        }
        return false;
    }
}
