using System.Collections.Concurrent;
using System.Net;
using Syllm.Brickly.Sdk.Grpc;
using System.Security.Cryptography;
using System.Threading.Channels;
using Brickly.Runtime.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>进程内假 Host：提供 RuntimeRegistry / Platform / Resource / Event / Connector / BrickStorage。</summary>
public sealed class FakeHost : IAsyncDisposable
{
    private WebApplication? _app;

    private FakeHost()
    {
    }

    public string Endpoint { get; private set; } = string.Empty;

    public string BootstrapToken { get; } = "bootstrap-token-test";

    public string RuntimeToHostToken { get; } = "runtime-to-host-test";

    public string HostToRuntimeToken { get; } = "host-to-runtime-test";

    public string RuntimeHandleId { get; set; } = "runtime-handle-1";

    // Registry
    public RegisterRequest? Register { get; private set; }

    public bool Unregistered { get; private set; }

    public string? RegisterBootstrapToken { get; private set; }

    public string? UnregisterRuntimeToken { get; private set; }

    // Platform
    public List<PlatformCallRequest> PlatformCalls { get; } = new();

    public Dictionary<string, Func<BrickValue, object?>> PlatformCallHandlers { get; } = new();

    public Func<PlatformCallRequest, object?>? DefaultPlatformCall { get; set; }

    public List<FakeInteractSession> PlatformSessions { get; } = new();

    public Func<BrickValue, Task<BrickValue>>? PlatformInteractRequestHandler { get; set; }

    public Func<BrickValue>? PlatformInteractFinalResult { get; set; }

    public List<string?> PlatformCallInvocationIds { get; } = new();

    // Events
    public List<PublishEventRequest> PublishedEvents { get; } = new();

    /// <summary>已订阅的主题。Runtime 启动时会并发拉起多条订阅流，必须线程安全。</summary>
    public ConcurrentQueue<string> SubscribedTopics { get; } = new();

    // Connector
    public List<ConnectorInvokeRequest> ConnectorInvokes { get; } = new();

    public Dictionary<(string BrickId, string CommandId), Func<BrickValue, object?>> ConnectorInvokeHandlers { get; } = new();

    public Func<ConnectorInvokeRequest, object?>? DefaultConnectorInvoke { get; set; }

    public List<FakeInteractSession> ConnectorSessions { get; } = new();

    public Func<BrickValue, Task<BrickValue>>? ConnectorInteractRequestHandler { get; set; }

    public Func<BrickValue>? ConnectorInteractFinalResult { get; set; }

    public List<string?> ConnectorInvokeInvocationIds { get; } = new();

    public List<string?> ConnectorInteractIntents { get; } = new();

    public List<ConnectorStartRequest> StartedDependencies { get; } = new();

    public List<string?> StartedInvocationIds { get; } = new();

    public List<(string HandleId, bool Stop)> DisposedDependencies { get; } = new();

    // Resources
    public ConcurrentDictionary<string, ResourceEntry> Resources { get; } = new();

    public List<ResourceCreateHeader> CreateHeaders { get; } = new();

    public List<string?> CreateInvocationIds { get; } = new();

    public List<int> CreateChunkCounts { get; } = new();

    public List<string> RevokedResources { get; } = new();

    public bool FailCreate { get; set; }

    // Storage
    public ConcurrentDictionary<string, BrickValue> StorageKv { get; } = new();

    public List<(string Scope, string Collection, BrickStorageDoc Doc)> Documents { get; } = new();

    public List<BrickStorageChangeEvent> WatchEvents { get; } = new();

    public static async Task<FakeHost> StartAsync()
    {
        var host = new FakeHost();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "FakeHost",
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = 16 * 1024 * 1024;
            grpc.MaxSendMessageSize = 16 * 1024 * 1024;
        });
        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton<FakeRegistryService>();
        builder.Services.AddSingleton<FakePlatformService>();
        builder.Services.AddSingleton<FakeResourceService>();
        builder.Services.AddSingleton<FakeEventService>();
        builder.Services.AddSingleton<FakeConnectorService>();
        builder.Services.AddSingleton<FakeBrickStorageService>();

        var app = builder.Build();
        app.MapGrpcService<FakeRegistryService>();
        app.MapGrpcService<FakePlatformService>();
        app.MapGrpcService<FakeResourceService>();
        app.MapGrpcService<FakeEventService>();
        app.MapGrpcService<FakeConnectorService>();
        app.MapGrpcService<FakeBrickStorageService>();

        await app.StartAsync().ConfigureAwait(false);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault() ?? throw new InvalidOperationException("FakeHost 未监听");
        var uri = new Uri(address);
        host._app = app;
        host.Endpoint = $"{uri.Host}:{uri.Port}";
        return host;
    }

    /// <summary>写入 Runtime 启动环境变量。</summary>
    public void ApplyEnvironment(string? dependencyBindings = null, string? profileConfig = null)
    {
        Environment.SetEnvironmentVariable("BRICKLY_HOST_ENDPOINT", Endpoint);
        Environment.SetEnvironmentVariable("BRICKLY_BOOTSTRAP_TOKEN", BootstrapToken);
        Environment.SetEnvironmentVariable("BRICKLY_RUNTIME_TO_HOST_TOKEN", RuntimeToHostToken);
        Environment.SetEnvironmentVariable("BRICKLY_HOST_TO_RUNTIME_TOKEN", HostToRuntimeToken);
        Environment.SetEnvironmentVariable("BRICKLY_DEPENDENCY_BINDINGS", dependencyBindings);
        Environment.SetEnvironmentVariable("BRICKLY_PROFILE_CONFIG", profileConfig);
    }

    public static void ClearEnvironment()
    {
        foreach (var name in new[]
        {
            "BRICKLY_HOST_ENDPOINT",
            "BRICKLY_BOOTSTRAP_TOKEN",
            "BRICKLY_RUNTIME_TO_HOST_TOKEN",
            "BRICKLY_HOST_TO_RUNTIME_TOKEN",
            "BRICKLY_DEPENDENCY_BINDINGS",
            "BRICKLY_PROFILE_CONFIG",
            "BRICKLY_PROFILE_ID",
        })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var app = _app;
        if (app is null)
        {
            return;
        }
        _app = null;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await app.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public sealed class ResourceEntry
    {
        public required string ResourceId { get; init; }
        public required byte[] Data { get; init; }
        public required string Sha256 { get; init; }
        public string? MediaType { get; init; }
        public string? Name { get; init; }
    }

    private sealed class FakeRegistryService : RuntimeRegistry.RuntimeRegistryBase
    {
        private readonly FakeHost _host;

        public FakeRegistryService(FakeHost host)
        {
            _host = host;
        }

        public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
        {
            _host.Register = request;
            _host.RegisterBootstrapToken = context.RequestHeaders.GetValue("x-brickly-bootstrap-token");
            return Task.FromResult(new RegisterResponse { RuntimeHandleId = _host.RuntimeHandleId });
        }

        public override Task<Empty> Unregister(UnregisterRequest request, ServerCallContext context)
        {
            _host.Unregistered = true;
            _host.UnregisterRuntimeToken = context.RequestHeaders.GetValue("x-brickly-runtime-token");
            return Task.FromResult(new Empty());
        }
    }

    private sealed class FakePlatformService : PlatformService.PlatformServiceBase
    {
        private readonly FakeHost _host;

        public FakePlatformService(FakeHost host)
        {
            _host = host;
        }

        public override Task<PlatformCallResponse> Call(PlatformCallRequest request, ServerCallContext context)
        {
            _host.PlatformCalls.Add(request);
            _host.PlatformCallInvocationIds.Add(context.RequestHeaders.GetValue("x-brickly-invocation-id"));
            BrickValue result;
            if (_host.PlatformCallHandlers.TryGetValue(request.Method, out var handler))
            {
                result = BrickValueCodec.FromClr(handler(request.Input));
            }
            else if (_host.DefaultPlatformCall is not null)
            {
                result = BrickValueCodec.FromClr(_host.DefaultPlatformCall(request));
            }
            else
            {
                result = BrickValueCodec.NullValue();
            }
            return Task.FromResult(new PlatformCallResponse { Result = result });
        }

        public override Task Interact(
            IAsyncStreamReader<ClientFrame> requestStream,
            IServerStreamWriter<ServerFrame> responseStream,
            ServerCallContext context)
        {
            var session = new FakeInteractSession
            {
                RequestHandler = _host.PlatformInteractRequestHandler,
                FinalResult = _host.PlatformInteractFinalResult,
            };
            _host.PlatformSessions.Add(session);
            return FakeInteractSessionRunner.RunAsync(requestStream, responseStream, context, session);
        }
    }

    private sealed class FakeResourceService : ResourceService.ResourceServiceBase
    {
        private const int ChunkBytes = 1024 * 1024;

        private readonly FakeHost _host;

        public FakeResourceService(FakeHost host)
        {
            _host = host;
        }

        public override async Task<global::Brickly.Runtime.V1.ResourceRef> Create(
            IAsyncStreamReader<ResourceWriteFrame> requestStream,
            ServerCallContext context)
        {
            ResourceCreateHeader? header = null;
            using var buffer = new MemoryStream();
            ulong expectedOffset = 0;
            var chunkCount = 0;
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (frame.Header is not null)
                {
                    header = frame.Header;
                    _host.CreateHeaders.Add(header);
                    _host.CreateInvocationIds.Add(context.RequestHeaders.GetValue("x-brickly-invocation-id"));
                }
                if (frame.Chunk is not null)
                {
                    chunkCount++;
                    if (frame.Chunk.Offset != expectedOffset)
                    {
                        throw new RpcException(new global::Grpc.Core.Status(StatusCode.InvalidArgument, "offset mismatch"));
                    }
                    buffer.Write(frame.Chunk.Data.Span);
                    expectedOffset += (ulong)frame.Chunk.Data.Length;
                }
            }

            if (_host.FailCreate)
            {
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.ResourceExhausted, "create failed"));
            }

            _host.CreateChunkCounts.Add(chunkCount);
            var data = buffer.ToArray();
            var id = "res_" + Guid.NewGuid().ToString("N")[..22];
            var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            _host.Resources[id] = new ResourceEntry
            {
                ResourceId = id,
                Data = data,
                Sha256 = sha,
                MediaType = header?.MediaType,
                Name = header?.Name,
            };

            var reference = new global::Brickly.Runtime.V1.ResourceRef
            {
                ResourceId = id,
                SizeBytes = (ulong)data.Length,
                Sha256 = ByteString.CopyFrom(Convert.FromHexString(sha)),
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
            };
            if (!string.IsNullOrEmpty(header?.MediaType))
            {
                reference.MediaType = header!.MediaType;
            }
            if (!string.IsNullOrEmpty(header?.Name))
            {
                reference.Name = header!.Name;
            }
            return reference;
        }

        public override async Task Read(
            ResourceReadRequest request,
            IServerStreamWriter<ResourceChunk> responseStream,
            ServerCallContext context)
        {
            if (!_host.Resources.TryGetValue(request.ResourceId, out var entry))
            {
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.NotFound, "resource not found"));
            }
            for (var offset = 0; offset < entry.Data.Length; offset += ChunkBytes)
            {
                var size = Math.Min(ChunkBytes, entry.Data.Length - offset);
                await responseStream.WriteAsync(new ResourceChunk
                {
                    Data = ByteString.CopyFrom(entry.Data, offset, size),
                }).ConfigureAwait(false);
            }
        }

        public override Task<ResourceMetadata> Stat(ResourceStatRequest request, ServerCallContext context)
        {
            if (!_host.Resources.TryGetValue(request.ResourceId, out var entry))
            {
                throw new RpcException(new global::Grpc.Core.Status(StatusCode.NotFound, "resource not found"));
            }
            return Task.FromResult(new ResourceMetadata
            {
                Resource = new global::Brickly.Runtime.V1.ResourceRef
                {
                    ResourceId = entry.ResourceId,
                    SizeBytes = (ulong)entry.Data.Length,
                    Sha256 = ByteString.CopyFrom(Convert.FromHexString(entry.Sha256)),
                },
                State = ResourceState.Available,
            });
        }

        public override Task<Empty> Revoke(ResourceRevokeRequest request, ServerCallContext context)
        {
            _host.Resources.TryRemove(request.ResourceId, out _);
            _host.RevokedResources.Add(request.ResourceId);
            return Task.FromResult(new Empty());
        }
    }

    private sealed class FakeEventService : EventService.EventServiceBase
    {
        private readonly FakeHost _host;

        public FakeEventService(FakeHost host)
        {
            _host = host;
        }

        public override Task<Empty> Publish(PublishEventRequest request, ServerCallContext context)
        {
            _host.PublishedEvents.Add(request);
            return Task.FromResult(new Empty());
        }

        public override async Task Subscribe(
            SubscribeEventsRequest request,
            IServerStreamWriter<DomainEvent> responseStream,
            ServerCallContext context)
        {
            // 先注册通道再记录 topic：否则测试看到 SubscribedTopics 后立即推送的事件可能丢失。
            var channel = Channel.CreateUnbounded<DomainEvent>();
            _host._eventChannels.TryAdd(channel, 0);
            _host.SubscribedTopics.Enqueue(request.Topic);
            try
            {
                await foreach (var domainEvent in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(domainEvent).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _host._eventChannels.TryRemove(channel, out _);
            }
        }
    }

    private readonly ConcurrentDictionary<Channel<DomainEvent>, byte> _eventChannels = new();

    public void PushDomainEvent(string topic, BrickValue payload)
    {
        foreach (var channel in _eventChannels.Keys)
        {
            channel.Writer.TryWrite(new DomainEvent { Topic = topic, Payload = payload });
        }
    }

    private sealed class FakeConnectorService : BrickConnectorService.BrickConnectorServiceBase
    {
        private readonly FakeHost _host;

        public FakeConnectorService(FakeHost host)
        {
            _host = host;
        }

        public override Task<InvokeResult> Invoke(ConnectorInvokeRequest request, ServerCallContext context)
        {
            _host.ConnectorInvokes.Add(request);
            _host.ConnectorInvokeInvocationIds.Add(context.RequestHeaders.GetValue("x-brickly-invocation-id"));
            BrickValue result;
            if (_host.ConnectorInvokeHandlers.TryGetValue((request.BrickId, request.CommandId), out var handler))
            {
                result = BrickValueCodec.FromClr(handler(request.Input));
            }
            else if (_host.DefaultConnectorInvoke is not null)
            {
                result = BrickValueCodec.FromClr(_host.DefaultConnectorInvoke(request));
            }
            else
            {
                result = BrickValueCodec.NullValue();
            }
            return Task.FromResult(new InvokeResult { Result = result });
        }

        public override Task Interact(
            IAsyncStreamReader<ClientFrame> requestStream,
            IServerStreamWriter<ServerFrame> responseStream,
            ServerCallContext context)
        {
            _host.ConnectorInteractIntents.Add(context.RequestHeaders.GetValue("x-brickly-intent"));
            var session = new FakeInteractSession
            {
                RequestHandler = _host.ConnectorInteractRequestHandler,
                FinalResult = _host.ConnectorInteractFinalResult,
            };
            _host.ConnectorSessions.Add(session);
            return FakeInteractSessionRunner.RunAsync(requestStream, responseStream, context, session);
        }

        public override Task<ConnectorStartResponse> Start(ConnectorStartRequest request, ServerCallContext context)
        {
            _host.StartedDependencies.Add(request);
            _host.StartedInvocationIds.Add(context.RequestHeaders.GetValue("x-brickly-invocation-id"));
            return Task.FromResult(new ConnectorStartResponse
            {
                HandleId = "handle-" + _host.StartedDependencies.Count,
            });
        }

        public override Task<Empty> Dispose(ConnectorDisposeRequest request, ServerCallContext context)
        {
            _host.DisposedDependencies.Add((request.HandleId, request.Stop));
            return Task.FromResult(new Empty());
        }
    }

    private sealed class FakeBrickStorageService : BrickStorageService.BrickStorageServiceBase
    {
        private readonly FakeHost _host;

        public FakeBrickStorageService(FakeHost host)
        {
            _host = host;
        }

        private int _docSequence;

        private string KvKey(BrickStorageScope scope, string key) => $"{scope}:{key}";

        // 对齐主进程 asDocument：id / revision / updatedAt 合并进 data 后再返回；
        // .NET SDK 直接把 Data 当作文档字典（见 HostBrickStorageClient.DocToMap）。
        private static BrickStorageDoc BuildDoc(
            string id,
            string revision,
            IReadOnlyDictionary<string, object?> userData)
        {
            var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var merged = new Dictionary<string, object?>(userData)
            {
                ["id"] = id,
                ["revision"] = revision,
                ["updatedAt"] = updatedAt,
            };
            return new BrickStorageDoc
            {
                Id = id,
                Revision = revision,
                UpdatedAt = updatedAt,
                Data = BrickValueCodec.FromClr(merged),
            };
        }

        // 对应主进程 omitMeta：剥离 id / revision / updatedAt，只保留用户字段。
        private static Dictionary<string, object?> UserData(BrickValue? value)
        {
            var record = value is null
                ? new Dictionary<string, object?>()
                : BrickValueCodec.ToClr(value) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            record.Remove("id");
            record.Remove("revision");
            record.Remove("updatedAt");
            return record;
        }

        private (string Scope, string Collection, BrickStorageDoc Doc)? FindDoc(
            BrickStorageScope scope,
            string collection,
            string id)
        {
            var scopeName = scope.ToString();
            foreach (var item in _host.Documents)
            {
                if (item.Scope == scopeName && item.Collection == collection && item.Doc.Id == id)
                {
                    return item;
                }
            }
            return null;
        }

        public override Task<BrickStorageKvGetResponse> KvGet(
            BrickStorageKvGetRequest request,
            ServerCallContext context)
        {
            if (_host.StorageKv.TryGetValue(KvKey(request.Scope, request.Key), out var value))
            {
                return Task.FromResult(new BrickStorageKvGetResponse { Found = true, Value = value });
            }
            return Task.FromResult(new BrickStorageKvGetResponse { Found = false });
        }

        public override Task<Empty> KvSet(BrickStorageKvSetRequest request, ServerCallContext context)
        {
            _host.StorageKv[KvKey(request.Scope, request.Key)] = request.Value;
            return Task.FromResult(new Empty());
        }

        public override Task<BrickStorageDeleteResponse> KvDelete(
            BrickStorageKvKeyRequest request,
            ServerCallContext context)
        {
            var deleted = _host.StorageKv.TryRemove(KvKey(request.Scope, request.Key), out _);
            return Task.FromResult(new BrickStorageDeleteResponse { Deleted = deleted });
        }

        public override Task<BrickStorageHasResponse> KvHas(
            BrickStorageKvKeyRequest request,
            ServerCallContext context)
        {
            return Task.FromResult(new BrickStorageHasResponse
            {
                Found = _host.StorageKv.ContainsKey(KvKey(request.Scope, request.Key)),
            });
        }

        public override Task<BrickStorageKvListResponse> KvList(
            BrickStorageKvListRequest request,
            ServerCallContext context)
        {
            var prefix = $"{request.Scope}:{request.Prefix}";
            var response = new BrickStorageKvListResponse();
            // 生产由 LevelDB 顺序扫描返回，按 key 升序；这里同样排序。
            response.Keys.Add(_host.StorageKv.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[(key.IndexOf(':') + 1)..])
                .OrderBy(key => key, StringComparer.Ordinal));
            return Task.FromResult(response);
        }

        public override Task<BrickStorageDoc> CreateDoc(
            BrickStorageCreateDocRequest request,
            ServerCallContext context)
        {
            var id = "doc-" + (++_docSequence);
            var doc = BuildDoc(id, NextRevision(), UserData(request.Data));
            _host.Documents.Add((request.Scope.ToString(), request.Collection, doc));
            return Task.FromResult(doc);
        }

        public override Task<BrickStorageGetDocResponse> GetDoc(
            BrickStorageDocKeyRequest request,
            ServerCallContext context)
        {
            var found = FindDoc(request.Scope, request.Collection, request.Id);
            return Task.FromResult(new BrickStorageGetDocResponse
            {
                Found = found is not null,
                Doc = found?.Doc,
            });
        }

        public override Task<BrickStorageDoc> PutDoc(BrickStoragePutDocRequest request, ServerCallContext context)
        {
            var raw = BrickValueCodec.ToClr(request.Doc) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
            var id = raw.TryGetValue("id", out var idValue) ? Convert.ToString(idValue) ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                // 生产：assertStorageKey(doc.id) 失败 → STORAGE_INVALID_KEY。
                throw BrickErrorStatus.ToRpcException("STORAGE_INVALID_KEY", "id 非法");
            }
            var doc = BuildDoc(id, NextRevision(), UserData(request.Doc));
            var existing = FindDoc(request.Scope, request.Collection, id);
            if (existing is not null)
            {
                _host.Documents[_host.Documents.IndexOf(existing.Value)] = (existing.Value.Scope, existing.Value.Collection, doc);
            }
            else
            {
                _host.Documents.Add((request.Scope.ToString(), request.Collection, doc));
            }
            return Task.FromResult(doc);
        }

        public override Task<BrickStorageDoc> UpdateDoc(BrickStorageUpdateDocRequest request, ServerCallContext context)
        {
            var existing = FindDoc(request.Scope, request.Collection, request.Id);
            if (existing is null)
            {
                // 生产：updateDoc 先 getDoc，缺失抛 STORAGE_NOT_FOUND。
                throw BrickErrorStatus.ToRpcException("STORAGE_NOT_FOUND", "文档不存在");
            }
            var merged = UserData(existing.Value.Doc.Data);
            foreach (var pair in UserData(request.Patch))
            {
                merged[pair.Key] = pair.Value;
            }
            var doc = BuildDoc(request.Id, NextRevision(), merged);
            _host.Documents[_host.Documents.IndexOf(existing.Value)] = (existing.Value.Scope, existing.Value.Collection, doc);
            return Task.FromResult(doc);
        }

        public override Task<BrickStorageDeleteResponse> DeleteDoc(
            BrickStorageDocKeyRequest request,
            ServerCallContext context)
        {
            var existing = FindDoc(request.Scope, request.Collection, request.Id);
            if (existing is null)
            {
                return Task.FromResult(new BrickStorageDeleteResponse { Deleted = false });
            }
            _host.Documents.Remove(existing.Value);
            return Task.FromResult(new BrickStorageDeleteResponse { Deleted = true });
        }

        public override Task<BrickStorageListDocsResponse> ListDocs(
            BrickStorageListDocsRequest request,
            ServerCallContext context)
        {
            var scopeName = request.Scope.ToString();
            IEnumerable<BrickStorageDoc> docs = _host.Documents
                .Where(item => item.Scope == scopeName && item.Collection == request.Collection)
                .Select(item => item.Doc)
                .OrderBy(doc => doc.Id, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(request.Prefix))
            {
                docs = docs.Where(doc => doc.Id.StartsWith(request.Prefix, StringComparison.Ordinal));
            }
            if (!string.IsNullOrEmpty(request.After))
            {
                docs = docs.Where(doc => string.CompareOrdinal(doc.Id, request.After) > 0);
            }
            var equals = UserData(request.Equals_);
            if (equals.Count > 0)
            {
                docs = docs.Where(doc => MatchesEquals(doc, equals));
            }
            if (request.Limit > 0)
            {
                docs = docs.Take((int)request.Limit);
            }
            var response = new BrickStorageListDocsResponse();
            response.Docs.Add(docs);
            return Task.FromResult(response);
        }

        private static bool MatchesEquals(BrickStorageDoc doc, Dictionary<string, object?> equals)
        {
            var data = BrickValueCodec.ToClr(doc.Data) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
            return equals.All(pair => data.TryGetValue(pair.Key, out var actual) && Equals(actual, pair.Value));
        }

        private static string NextRevision() => "r_" + Guid.NewGuid().ToString("N")[..16];

        public override Task WatchDocs(
            BrickStorageWatchRequest request,
            IServerStreamWriter<BrickStorageChangeEvent> responseStream,
            ServerCallContext context)
        {
            return WatchCore(request, responseStream, context);
        }

        public override Task KvWatch(
            BrickStorageWatchRequest request,
            IServerStreamWriter<BrickStorageChangeEvent> responseStream,
            ServerCallContext context)
        {
            return WatchCore(request, responseStream, context);
        }

        private async Task WatchCore(
            BrickStorageWatchRequest request,
            IServerStreamWriter<BrickStorageChangeEvent> responseStream,
            ServerCallContext context)
        {
            var channel = Channel.CreateUnbounded<BrickStorageChangeEvent>();
            _host._watchChannels.TryAdd(channel, 0);
            try
            {
                await foreach (var change in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(change).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _host._watchChannels.TryRemove(channel, out _);
            }
        }

        public override Task<BrickStorageStatus> Status(Empty request, ServerCallContext context)
        {
            // 生产当前恒返回 signedIn=false / pendingWrites=0；这里故意用非默认值，验证 SDK 原样透传。
            return Task.FromResult(new BrickStorageStatus
            {
                UsedBytes = 0,
                QuotaBytes = 1024 * 1024,
                SignedIn = true,
                PendingWrites = 0,
            });
        }
    }

    private readonly ConcurrentDictionary<Channel<BrickStorageChangeEvent>, byte> _watchChannels = new();

    public int WatchSubscribers => _watchChannels.Count;

    public void PushWatchEvent(BrickStorageChangeEvent change)
    {
        WatchEvents.Add(change);
        foreach (var channel in _watchChannels.Keys)
        {
            channel.Writer.TryWrite(change);
        }
    }
}
