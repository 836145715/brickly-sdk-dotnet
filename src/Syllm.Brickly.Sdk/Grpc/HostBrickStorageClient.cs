using Brickly.Runtime.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Runtime → Host BrickStorageService 客户端。</summary>
internal sealed class HostBrickStorageClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly BrickStorageService.BrickStorageServiceClient _client;
    private readonly string _token;

    public HostBrickStorageClient(string endpoint, string runtimeToHostToken)
    {
        _channel = ChannelFactory.Create(endpoint, RuntimeMetadata.InvokeMaxBytes);
        _client = new BrickStorageService.BrickStorageServiceClient(_channel);
        _token = runtimeToHostToken;
    }

    public async Task<(object? Value, bool Found)> KvGetAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        var response = await _client
            .KvGetAsync(
                new BrickStorageKvGetRequest { Scope = ToScope(scope), Key = key },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.Found)
        {
            return (null, false);
        }
        return (BrickValueCodec.ToClr(response.Value), true);
    }

    public async Task KvSetAsync(string scope, string key, object? value, CancellationToken cancellationToken)
    {
        var request = new BrickStorageKvSetRequest
        {
            Scope = ToScope(scope),
            Key = key,
            Value = BrickValueCodec.FromClr(value),
        };
        await _client.KvSetAsync(request, Auth(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> KvDeleteAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var response = await _client
            .KvDeleteAsync(
                new BrickStorageKvKeyRequest { Scope = ToScope(scope), Key = key },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Deleted;
    }

    public async Task<bool> KvHasAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var response = await _client
            .KvHasAsync(
                new BrickStorageKvKeyRequest { Scope = ToScope(scope), Key = key },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Found;
    }

    public async Task<List<string>> KvListAsync(string scope, string prefix, CancellationToken cancellationToken)
    {
        var response = await _client
            .KvListAsync(
                new BrickStorageKvListRequest { Scope = ToScope(scope), Prefix = prefix },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Keys.ToList();
    }

    public async Task<Dictionary<string, object?>?> GetDocAsync(
        string scope,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        var response = await _client
            .GetDocAsync(
                new BrickStorageDocKeyRequest { Scope = ToScope(scope), Collection = collection, Id = id },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Found ? DocToMap(response.Doc) : null;
    }

    public async Task<Dictionary<string, object?>> CreateDocAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken cancellationToken)
    {
        var request = new BrickStorageCreateDocRequest
        {
            Scope = ToScope(scope),
            Collection = collection,
            Data = BrickValueCodec.FromClr(data),
        };
        var response = await _client
            .CreateDocAsync(request, Auth(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return DocToMap(response) ?? new Dictionary<string, object?>();
    }

    public async Task<Dictionary<string, object?>> PutDocAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> doc,
        CancellationToken cancellationToken)
    {
        var request = new BrickStoragePutDocRequest
        {
            Scope = ToScope(scope),
            Collection = collection,
            Doc = BrickValueCodec.FromClr(doc),
        };
        var response = await _client
            .PutDocAsync(request, Auth(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return DocToMap(response) ?? new Dictionary<string, object?>();
    }

    public async Task<Dictionary<string, object?>> UpdateDocAsync(
        string scope,
        string collection,
        string id,
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken)
    {
        var request = new BrickStorageUpdateDocRequest
        {
            Scope = ToScope(scope),
            Collection = collection,
            Id = id,
            Patch = BrickValueCodec.FromClr(patch),
        };
        var response = await _client
            .UpdateDocAsync(request, Auth(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return DocToMap(response) ?? new Dictionary<string, object?>();
    }

    public async Task<bool> DeleteDocAsync(
        string scope,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        var response = await _client
            .DeleteDocAsync(
                new BrickStorageDocKeyRequest { Scope = ToScope(scope), Collection = collection, Id = id },
                Auth(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Deleted;
    }

    public async Task<List<Dictionary<string, object?>>> ListDocsAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> query,
        CancellationToken cancellationToken)
    {
        query.TryGetValue("prefix", out var prefixValue);
        query.TryGetValue("after", out var afterValue);
        var request = new BrickStorageListDocsRequest
        {
            Scope = ToScope(scope),
            Collection = collection,
            Prefix = prefixValue as string ?? string.Empty,
            After = afterValue as string ?? string.Empty,
        };
        if (query.TryGetValue("equals", out var equalsValue) && equalsValue is not null)
        {
            request.Equals_ = BrickValueCodec.FromClr(equalsValue);
        }
        if (TryInt64(query, "limit", out var limit) && limit >= 0)
        {
            request.Limit = (uint)limit;
        }

        var response = await _client
            .ListDocsAsync(request, Auth(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var docs = new List<Dictionary<string, object?>>(response.Docs.Count);
        foreach (var doc in response.Docs)
        {
            docs.Add(DocToMap(doc) ?? new Dictionary<string, object?>());
        }
        return docs;
    }

    public async Task<Dictionary<string, object?>> StatusAsync(CancellationToken cancellationToken)
    {
        var response = await _client
            .StatusAsync(new Empty(), Auth(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["usedBytes"] = response.UsedBytes,
            ["quotaBytes"] = response.QuotaBytes,
            ["signedIn"] = response.SignedIn,
            ["pendingWrites"] = response.PendingWrites,
        };
    }

    public async Task<IDisposable> WatchDocsAsync(
        string scope,
        string collection,
        Action<Dictionary<string, object?>> handler,
        CancellationToken cancellationToken)
    {
        var request = new BrickStorageWatchRequest { Scope = ToScope(scope), Collection = collection };
        var call = _client.WatchDocs(request, Auth(), cancellationToken: cancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var change = call.ResponseStream.Current;
                    var payload = new Dictionary<string, object?>
                    {
                        ["type"] = change.Type,
                        ["id"] = change.Id,
                    };
                    if (change.Doc is not null)
                    {
                        payload["doc"] = BrickValueCodec.ToClr(change.Doc);
                    }
                    handler(payload);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RpcException)
            {
            }
        });
        return new WatchSubscription(call);
    }

    public void Dispose() => _channel.Dispose();

    private Metadata Auth() => new() { { RuntimeMetadata.RuntimeToken, _token } };

    private static BrickStorageScope ToScope(string scope) => scope switch
    {
        "local" => BrickStorageScope.Local,
        "secret" => BrickStorageScope.Secret,
        _ => BrickStorageScope.User,
    };

    private static Dictionary<string, object?>? DocToMap(BrickStorageDoc? doc)
    {
        if (doc is null)
        {
            return null;
        }
        var value = BrickValueCodec.ToClr(doc.Data);
        if (value is Dictionary<string, object?> record)
        {
            return record;
        }
        return new Dictionary<string, object?>
        {
            ["id"] = doc.Id,
            ["revision"] = doc.Revision,
            ["updatedAt"] = doc.UpdatedAt,
        };
    }

    private static bool TryInt64(IReadOnlyDictionary<string, object?> query, string key, out long value)
    {
        value = 0;
        if (!query.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }
        switch (raw)
        {
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            case double d when d == Math.Truncate(d):
                value = (long)d;
                return true;
            case System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.Number:
                return element.TryGetInt64(out value);
            default:
                return false;
        }
    }

    private sealed class WatchSubscription : IDisposable
    {
        private readonly AsyncServerStreamingCall<BrickStorageChangeEvent> _call;

        public WatchSubscription(AsyncServerStreamingCall<BrickStorageChangeEvent> call)
        {
            _call = call;
        }

        public void Dispose() => _call.Dispose();
    }
}
