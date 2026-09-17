using System.Text.Json;
using Syllm.Brickly.Sdk.Grpc;
using Xunit;

namespace Syllm.Brickly.Sdk.Tests;

public sealed class SearchTests
{
    private static CommandContext Ctx() =>
        new(
            new BricklyRuntime(),
            "req-1",
            "search-files",
            new CommandInvocationContext(),
            null,
            CancellationToken.None,
            UnaryCommandStream.Instance);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static readonly IReadOnlyList<SearchResultItem> OneResult =
    [
        new SearchResultItem
        {
            Id = "f1",
            Title = "a.txt",
            Activate = new SearchRouteRef
            {
                Command = "open-file",
                Input = new Dictionary<string, object?> { ["path"] = "a.txt" },
            },
        },
    ];

    [Fact]
    public async Task AdaptParsesFixedInput()
    {
        SearchContext? seen = null;
        var handler = SearchBinding.Adapt(ctx =>
        {
            seen = ctx;
            return Task.FromResult(OneResult);
        });

        var output = await handler(
            Ctx(),
            Json("""
                {
                    "providerId": "search-files",
                    "query": "a.txt",
                    "sequence": 3,
                    "limit": 8,
                    "caller": { "brickId": "com.brickly.quick-search", "origin": "platform", "version": "0.0.0" }
                }
                """));

        Assert.NotNull(seen);
        Assert.Equal("a.txt", seen.Query);
        Assert.Equal(3, seen.Sequence);
        Assert.Equal(8, seen.Limit);
        Assert.Equal("com.brickly.quick-search", seen.Caller?.BrickId);
        Assert.Equal("platform", seen.Caller?.Origin);
        Assert.Equal("req-1", seen.RequestID);
        Assert.Equal("search-files", seen.CommandID);

        var map = Assert.IsType<Dictionary<string, object?>>(output);
        var results = Assert.IsAssignableFrom<IReadOnlyList<SearchResultItem>>(map["results"]);
        var item = Assert.Single(results);
        Assert.Equal("f1", item.Id);
        Assert.Equal("open-file", item.Activate?.Command);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("{\"query\": 1}")]
    [InlineData("{}")]
    public async Task AdaptRejectsMalformedInput(string raw)
    {
        var handler = SearchBinding.Adapt(_ => Task.FromResult<IReadOnlyList<SearchResultItem>>([]));
        var error = await Assert.ThrowsAsync<BppException>(() => handler(Ctx(), Json(raw)));
        Assert.Equal(BppErrorCodes.InvalidInput, error.Code);
    }

    [Fact]
    public async Task AdaptValidatesResultShape()
    {
        var handler = SearchBinding.Adapt(_ =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(
                [new SearchResultItem { Id = "", Title = "x" }]));
        var error = await Assert.ThrowsAsync<BppException>(() => handler(Ctx(), Json("{\"query\":\"a\"}")));
        Assert.Equal(BppErrorCodes.Internal, error.Code);
    }

    [Fact]
    public async Task AdaptRejectsBadIcon()
    {
        var handler = SearchBinding.Adapt(_ =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(
                [new SearchResultItem { Id = "f1", Title = "a.txt", Icon = 123 }]));
        var error = await Assert.ThrowsAsync<BppException>(() => handler(Ctx(), Json("{\"query\":\"a\"}")));
        Assert.Equal(BppErrorCodes.Internal, error.Code);
    }

    [Fact]
    public async Task AdaptAcceptsResourceRefIcon()
    {
        var handler = SearchBinding.Adapt(_ =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(
                [
                    new SearchResultItem
                    {
                        Id = "f1",
                        Title = "a.txt",
                        Icon = new ResourceRef
                        {
                            ResourceId = "res-1",
                            SizeBytes = 4,
                            Sha256 = "abc",
                            ExpiresAt = 1,
                        },
                    },
                ]));
        var output = await handler(Ctx(), Json("{\"query\":\"a\"}"));
        var map = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SearchResultItem>>(map["results"]));
    }

    [Fact]
    public async Task AdaptDefaultsLimitAndToleratesBadCaller()
    {
        SearchContext? seen = null;
        var handler = SearchBinding.Adapt(ctx =>
        {
            seen = ctx;
            return Task.FromResult<IReadOnlyList<SearchResultItem>>([]);
        });
        await handler(Ctx(), Json("{\"query\":\"a\",\"limit\":\"x\",\"sequence\":\"y\",\"caller\":123}"));
        Assert.Equal(24, seen?.Limit);
        Assert.Equal(0, seen?.Sequence);
        Assert.Null(seen?.Caller);
    }

    [Fact]
    public async Task OnSearchRegistersIntoCommandHandlers()
    {
        var runtime = new BricklyRuntime();
        runtime.OnSearch("search-files", _ => Task.FromResult(OneResult));
        var dispatcher = (ICommandDispatcher)runtime;
        Assert.Contains("search-files", dispatcher.CommandIds);
        var output = await dispatcher.DispatchInvokeAsync(
            "search-files",
            Json("{\"query\":\"a\"}"),
            "req-9",
            CancellationToken.None);
        var map = Assert.IsType<Dictionary<string, object?>>(output);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SearchResultItem>>(map["results"]));
    }

    [Fact]
    public async Task SearchApiRoutesPlatformCall()
    {
        var runtime = new BricklyRuntime();
        Assert.NotNull(runtime.Platform.Search);
        // 未连接 PlatformService 时应返回 PROTOCOL_ERROR 而非 NullReference。
        await Assert.ThrowsAsync<BppException>(() =>
            runtime.Platform.Search.QueryAsync(new SearchQueryRequest { Query = "a" }));
        await Assert.ThrowsAsync<BppException>(() => runtime.Platform.Search.ActivateAsync("r1"));
        await Assert.ThrowsAsync<BppException>(() => runtime.Platform.Search.RunActionAsync("r1", "a1"));
    }
}
