using System.Text.Json;
using Brickly.Runtime.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Syllm.Brickly.Sdk.Grpc;

namespace Syllm.Brickly.Sdk;

/// <summary>
/// Brick Go/.NET runtime 主入口：连接 Host gRPC、注册 Runtime、分发 invoke / interact。
/// 缺 BRICKLY_HOST_ENDPOINT 时拒绝启动（无 BPP fallback）。
/// </summary>
public sealed class BricklyRuntime : ICommandDispatcher, IAsyncDisposable
{
    private const int MaxTerminalWindowEventIds = 1024;

    private static readonly string[] WindowHostEventTopics =
    {
        "window.notify",
        "window.request",
        "window.request.cancel",
        "window.closed",
        "window.focus",
        "window.blur",
        "window.resize",
        "window.move",
        "window.show",
        "window.hide",
    };

    private static readonly AsyncLocal<CommandScope?> CurrentScope = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, CommandHandler> _handlers = new();
    private readonly Dictionary<long, WindowHandle> _windows = new();
    private readonly Dictionary<string, IDisposable> _eventSubscriptions = new();
    private readonly HashSet<string> _terminalWindowEventIds = new();
    private readonly Queue<string> _terminalWindowEventOrder = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Func<Task>? _readyHandler;
    private Func<Task>? _shutdownHandler;
    private StartEnvironment? _environment;
    private RuntimeServer? _server;
    private HostPlatformClient? _platformClient;
    private HostResourceClient? _resourceClient;
    private HostBrickStorageClient? _storageClient;
    private GrpcChannel? _registryChannel;
    private RuntimeRegistry.RuntimeRegistryClient? _registry;
    private bool _started;
    private bool _disposed;

    public BricklyRuntime()
    {
        UI = new UI(this);
        Events = new EventBus(this);
        Platform = new PlatformApi(this);
        System = Platform.System;
        Dependencies = new DependencyRegistry(this);
        Storage = new StorageApi(this);
        Config = new Dictionary<string, object?>();
    }

    /// <summary>Brick 级 UI 门面（binding=session）。</summary>
    public UI UI { get; }

    /// <summary>Brick 级事件总线。</summary>
    public EventBus Events { get; }

    /// <summary>宿主平台能力门面。</summary>
    public PlatformApi Platform { get; }

    /// <summary>Platform.System 的便捷别名。</summary>
    public SystemApi System { get; }

    /// <summary>Host 注入的依赖绑定。</summary>
    public DependencyRegistry Dependencies { get; }

    /// <summary>本机持久存储。</summary>
    public StorageApi Storage { get; }

    /// <summary>Host 注入的 Profile 配置快照。</summary>
    public IReadOnlyDictionary<string, object?> Config { get; private set; }

    /// <summary>Host 注册返回的 Runtime handle id；未启动为 null。</summary>
    public string? RuntimeHandleId { get; private set; }

    /// <summary>Host 注入的 Profile ID；未注入为 null。</summary>
    public string? ProfileId { get; private set; }

    /// <summary>注册 command 处理器（链式）。</summary>
    public BricklyRuntime OnCommand(string commandId, CommandHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _handlers[commandId] = handler;
        }
        return this;
    }

    /// <summary>
    /// 注册搜索 Provider 端点处理器（对应 manifest provider 标记命令）。
    /// 入参/出参形状由协议定死：handler 收到 <see cref="SearchContext"/>，返回结果数组；
    /// 畸形时回传 INVALID_INPUT / INTERNAL。
    /// </summary>
    public BricklyRuntime OnSearch(string commandId, SearchHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(handler);
        return OnCommand(commandId, SearchBinding.Adapt(handler));
    }

    /// <summary>注册 ready 钩子（注册成功后异步触发）。</summary>
    public BricklyRuntime OnReady(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _readyHandler = handler;
        }
        return this;
    }

    /// <summary>注册 shutdown 钩子（DisposeAsync 时触发）。</summary>
    public BricklyRuntime OnShutdown(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _shutdownHandler = handler;
        }
        return this;
    }

    IReadOnlyList<string> ICommandDispatcher.CommandIds
    {
        get
        {
            lock (_sync)
            {
                return _handlers.Keys.ToArray();
            }
        }
    }

    /// <summary>
    /// 启动 Runtime：读取 Host 注入环境、起 Kestrel gRPC server、注册 Runtime。
    /// 注册成功后返回；进程存活请 await <see cref="WaitForShutdownAsync"/>。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_started)
            {
                throw new BppException(BppErrorCodes.ProtocolError, "Runtime 已启动");
            }
            _started = true;
        }

        var environment = RuntimeEnv.Take();
        if (environment is null)
        {
            lock (_sync)
            {
                _started = false;
            }
            throw new BppException(
                BppErrorCodes.ProtocolError,
                "BRICKLY_HOST_ENDPOINT 未注入；Runtime 只走 Host gRPC");
        }

        _environment = environment;
        Config = ProfileConfig.Read();
        ProfileId = ProfileConfig.ReadProfileId();
        var bindingsRaw = Environment.GetEnvironmentVariable(RuntimeMetadata.DependencyBindingsEnv);
        if (!string.IsNullOrEmpty(bindingsRaw))
        {
            Dependencies.ReplaceFromJson(bindingsRaw);
        }

        _platformClient = new HostPlatformClient(environment.HostEndpoint, environment.RuntimeToHostToken);
        _resourceClient = new HostResourceClient(environment.HostEndpoint, environment.RuntimeToHostToken);
        _storageClient = new HostBrickStorageClient(environment.HostEndpoint, environment.RuntimeToHostToken);

        try
        {
            _server = await RuntimeServer
                .StartAsync(
                    new RuntimeServerOptions
                    {
                        HostToRuntimeToken = environment.HostToRuntimeToken,
                        Dispatcher = this,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _registryChannel = ChannelFactory.Create(environment.HostEndpoint, RuntimeMetadata.InvokeMaxBytes);
            _registry = new RuntimeRegistry.RuntimeRegistryClient(_registryChannel);
            var request = new RegisterRequest
            {
                Endpoint = _server.Endpoint,
                Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
                Capabilities = new CapabilitySummary { SupportsInteract = true },
            };
            request.Capabilities.Commands.AddRange(((ICommandDispatcher)this).CommandIds);
            var metadata = new Metadata { { RuntimeMetadata.BootstrapToken, environment.BootstrapToken } };
            var response = await _registry
                .RegisterAsync(request, metadata, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            RuntimeHandleId = response.RuntimeHandleId;
        }
        catch (Exception)
        {
            await CleanupTransportAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _started = false;
            }
            throw;
        }

        AttachEventSubscriptions();
        Func<Task>? ready;
        lock (_sync)
        {
            ready = _readyHandler;
        }
        if (ready is not null)
        {
            _ = RunHookAsync(ready, "onReady");
        }
    }

    /// <summary>等待 Runtime 关闭（DisposeAsync 后完成）。</summary>
    public Task WaitForShutdownAsync() => _done.Task;

    /// <summary>再跑自己的一条命令；没有当前命令时是 root。</summary>
    public async Task<object?> InvokeAsync(
        string commandId,
        object? input,
        CancellationToken cancellationToken = default)
    {
        return await PlatformCallValueAsync(
            "runtime.invoke",
            new Dictionary<string, object?> { ["commandId"] = commandId, ["input"] = input },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>已有占用上再开会话；没有占用则拒绝。</summary>
    public async Task<Interaction> InteractAsync(
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        var session = await PlatformInteractAsync(commandId, input, null, cancellationToken).ConfigureAwait(false);
        InteractionSupport.Pump(session, options.OnEvent);
        return session;
    }

    /// <summary>CallAsync = Interact + 半关闭；必须与命令 mode=call 对齐。</summary>
    public async Task<object?> CallAsync(
        string commandId,
        object? input,
        CallOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        return await InteractionSupport
            .CallAsync(new RuntimeSelfClient(this), commandId, input, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>惰性绑定已有 ResourceRef，不立即访问 Host。</summary>
    public ResourceHandle OpenResource(ResourceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.IsValid())
        {
            throw new BppException(BppErrorCodes.InvalidResourceRef, "ResourceRef 格式无效");
        }
        var client = _resourceClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "ResourceService 未就绪");
        return new ResourceHandle(client, reference);
    }

    /// <summary>创建资源；超过 1 MiB 自动走 Writer。</summary>
    public Task<ResourceHandle> CreateResourceAsync(
        object content,
        ResourceCreateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateResourceCoreAsync(content, options, null, cancellationToken);

    /// <summary>从 Stream 流式创建资源。</summary>
    public Task<ResourceHandle> CreateResourceFromAsync(
        Stream source,
        ResourceCreateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateResourceFromCoreAsync(source, options, null, cancellationToken);

    /// <summary>多次 Write 聚合为 1 MiB 分块，Finish 后返回 Handle。</summary>
    public Task<ResourceWriter> CreateResourceWriterAsync(
        ResourceCreateOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateResourceWriterCoreAsync(options, null, cancellationToken);

    public void Debug(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        EmitLog("debug", message, null, fields, CurrentInvocationId);

    public void Info(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        EmitLog("info", message, null, fields, CurrentInvocationId);

    public void Warn(string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        EmitLog("warn", message, null, fields, CurrentInvocationId);

    public void Error(string message, Exception? error = null, IReadOnlyDictionary<string, object?>? fields = null) =>
        EmitLog("error", message, error, fields, CurrentInvocationId);

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Func<Task>? shutdown;
        lock (_sync)
        {
            shutdown = _shutdownHandler;
        }
        if (shutdown is not null)
        {
            try
            {
                await shutdown().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                EmitLog("error", "onShutdown error", error, null, null);
            }
        }

        var environment = _environment;
        if (_registry is not null && environment is not null)
        {
            try
            {
                var metadata = new Metadata { { RuntimeMetadata.RuntimeToken, environment.RuntimeToHostToken } };
                await _registry.UnregisterAsync(new UnregisterRequest(), metadata).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Host 可能已关闭。
            }
        }

        ClearWindows();
        ClearEventSubscriptions();
        await CleanupTransportAsync().ConfigureAwait(false);
        _shutdownCts.Cancel();
        _done.TrySetResult();
    }

    // —— ICommandDispatcher ——

    async Task<object?> ICommandDispatcher.DispatchInvokeAsync(
        string commandId,
        JsonElement input,
        string? invocationId,
        CancellationToken cancellationToken)
    {
        CommandHandler? handler;
        lock (_sync)
        {
            _handlers.TryGetValue(commandId, out handler);
        }
        if (handler is null)
        {
            if (commandId == "echo")
            {
                return input;
            }
            throw new BppException(BppErrorCodes.CommandNotFound, $"unknown command: {commandId}");
        }

        var requestId = string.IsNullOrEmpty(invocationId) ? "grpc-" + commandId : invocationId;
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new CommandContext(
            this,
            requestId,
            commandId,
            new CommandInvocationContext(),
            null,
            commandCts.Token,
            UnaryCommandStream.Instance);
        return await RunInScopeAsync(context, () => handler(context, input)).ConfigureAwait(false);
    }

    async Task<object?> ICommandDispatcher.DispatchInteractAsync(
        string commandId,
        InteractServerSession session)
    {
        CommandHandler? handler;
        lock (_sync)
        {
            _handlers.TryGetValue(commandId, out handler);
        }
        if (handler is null)
        {
            throw new BppException(BppErrorCodes.CommandNotFound, $"unknown command: {commandId}");
        }

        var requestId = string.IsNullOrEmpty(session.InvocationId) ? "grpc-" + commandId : session.InvocationId;
        var context = new CommandContext(
            this,
            requestId,
            commandId,
            new CommandInvocationContext(),
            null,
            session.ContextToken,
            new InteractCommandStream(session));
        return await RunInScopeAsync(context, () => handler(context, session.Initial)).ConfigureAwait(false);
    }

    // —— 出站调用 ——

    internal string? CurrentInvocationId => CurrentScope.Value?.RequestId;

    internal async Task<object?> ConnectorInvokeAsync(
        BrickRef reference,
        string commandId,
        object? input,
        string? invocationId,
        string? handleId,
        CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "Host Connector 未连接");
        using var link = LinkScope(cancellationToken);
        if (string.IsNullOrEmpty(handleId))
        {
            return await client
                .ConnectAsync(reference.BrickId, commandId, input, invocationId, null, link.Token)
                .ConfigureAwait(false);
        }
        return await client
            .ConnectOnHandleAsync(reference.BrickId, commandId, input, invocationId, handleId, null, link.Token)
            .ConfigureAwait(false);
    }

    internal Task<ConnectorInteraction> ConnectorInteractAsync(
        BrickRef reference,
        string commandId,
        object? input,
        string? invocationId,
        string? handleId,
        string? intent,
        CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "Host Connector 未连接");
        return client.InteractAsync(
            reference.BrickId,
            commandId,
            input,
            invocationId,
            handleId,
            intent,
            null,
            EffectiveToken(cancellationToken));
    }

    internal async Task<StartedToolHandle> StartDependencyAsync(string alias, BrickRef reference)
    {
        _ = alias;
        if (CurrentScope.Value is null || string.IsNullOrEmpty(CurrentInvocationId))
        {
            throw new BppException(
                BppErrorCodes.ParentInvocationRequired,
                "命令外不能 start 依赖。请先 InvokeAsync 进入自己的命令再 start。");
        }
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "Host Connector 未连接");
        var invocationId = CurrentInvocationId!;
        var handleId = await client
            .StartDependencyAsync(reference.BrickId, invocationId, CancellationToken.None)
            .ConfigureAwait(false);
        return new StartedToolHandle(this, reference, handleId, invocationId);
    }

    internal async Task DisposeStartedAsync(
        string handleId,
        string invocationId,
        bool stop,
        CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "Host Connector 未连接");
        await client
            .DisposeDependencyAsync(handleId, invocationId, stop, cancellationToken)
            .ConfigureAwait(false);
    }

    // —— 资源 ——

    internal Task<ResourceHandle> CreateResourceAsync(
        object content,
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken = default) =>
        CreateResourceCoreAsync(content, options, requestId, cancellationToken);

    internal Task<ResourceHandle> CreateResourceFromAsync(
        Stream source,
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken = default) =>
        CreateResourceFromCoreAsync(source, options, requestId, cancellationToken);

    internal Task<ResourceWriter> CreateResourceWriterAsync(
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken = default) =>
        CreateResourceWriterCoreAsync(options, requestId, cancellationToken);

    // —— Storage ——

    internal async Task<object?> StorageKvGetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var client = StorageClientOrThrow();
        var (value, found) = await client.KvGetAsync(scope, key, EffectiveToken(cancellationToken)).ConfigureAwait(false);
        return found ? value : null;
    }

    internal Task StorageKvSetAsync(string scope, string key, object? value, CancellationToken cancellationToken) =>
        StorageClientOrThrow().KvSetAsync(scope, key, value, EffectiveToken(cancellationToken));

    internal Task<bool> StorageKvDeleteAsync(string scope, string key, CancellationToken cancellationToken) =>
        StorageClientOrThrow().KvDeleteAsync(scope, key, EffectiveToken(cancellationToken));

    internal Task<bool> StorageKvHasAsync(string scope, string key, CancellationToken cancellationToken) =>
        StorageClientOrThrow().KvHasAsync(scope, key, EffectiveToken(cancellationToken));

    internal Task<List<string>> StorageKvListAsync(string scope, string prefix, CancellationToken cancellationToken) =>
        StorageClientOrThrow().KvListAsync(scope, prefix, EffectiveToken(cancellationToken));

    internal Task<Dictionary<string, object?>?> StorageGetDocAsync(
        string scope,
        string collection,
        string id,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().GetDocAsync(scope, collection, id, EffectiveToken(cancellationToken));

    internal Task<Dictionary<string, object?>> StorageCreateDocAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().CreateDocAsync(scope, collection, data, EffectiveToken(cancellationToken));

    internal Task<Dictionary<string, object?>> StoragePutDocAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> doc,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().PutDocAsync(scope, collection, doc, EffectiveToken(cancellationToken));

    internal Task<Dictionary<string, object?>> StorageUpdateDocAsync(
        string scope,
        string collection,
        string id,
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().UpdateDocAsync(scope, collection, id, patch, EffectiveToken(cancellationToken));

    internal Task<bool> StorageDeleteDocAsync(
        string scope,
        string collection,
        string id,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().DeleteDocAsync(scope, collection, id, EffectiveToken(cancellationToken));

    internal Task<List<Dictionary<string, object?>>> StorageListDocsAsync(
        string scope,
        string collection,
        IReadOnlyDictionary<string, object?> query,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().ListDocsAsync(scope, collection, query, EffectiveToken(cancellationToken));

    internal Task<Dictionary<string, object?>> StorageStatusAsync(CancellationToken cancellationToken) =>
        StorageClientOrThrow().StatusAsync(EffectiveToken(cancellationToken));

    internal Task<IDisposable> StorageWatchAsync(
        string scope,
        string collection,
        Action<Dictionary<string, object?>> handler,
        CancellationToken cancellationToken) =>
        StorageClientOrThrow().WatchDocsAsync(scope, collection, handler, EffectiveToken(cancellationToken));

    // —— Platform 调用 ——

    internal async Task<JsonElement> PlatformCallRawAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "PlatformService 未连接；gRPC Runtime 是唯一路径");
        using var link = LinkScope(cancellationToken);
        var value = await client
            .PlatformCallRawAsync(method, input, null, link.Token)
            .ConfigureAwait(false);
        return BrickValueCodec.ToJsonElement(value);
    }

    internal async Task<T> PlatformCallAsync<T>(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var element = await PlatformCallRawAsync(method, input, cancellationToken).ConfigureAwait(false);
        return element.Deserialize<T>(JsonDefaults.Options)
            ?? throw new BppException(BppErrorCodes.ProtocolError, $"PlatformService {method} 返回无效结果");
    }

    internal async Task<Dictionary<string, object?>> PlatformCallMapAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var value = await PlatformCallValueAsync(method, input, cancellationToken).ConfigureAwait(false);
        if (value is Dictionary<string, object?> map)
        {
            return map;
        }
        if (value is null)
        {
            return new Dictionary<string, object?>();
        }
        throw new BppException(BppErrorCodes.ProtocolError, $"PlatformService {method} 返回无效结果");
    }

    internal async Task<List<Dictionary<string, object?>>> PlatformCallListAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var value = await PlatformCallValueAsync(method, input, cancellationToken).ConfigureAwait(false);
        if (value is List<object?> items)
        {
            var result = new List<Dictionary<string, object?>>(items.Count);
            foreach (var item in items)
            {
                if (item is Dictionary<string, object?> map)
                {
                    result.Add(map);
                }
            }
            return result;
        }
        throw new BppException(BppErrorCodes.ProtocolError, $"PlatformService {method} 返回无效结果");
    }

    internal async Task<string> PlatformCallStringAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var value = await PlatformCallValueAsync(method, input, cancellationToken).ConfigureAwait(false);
        return value as string
            ?? throw new BppException(BppErrorCodes.ProtocolError, $"PlatformService {method} 返回无效结果");
    }

    internal async Task<bool> PlatformCallBoolAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        var value = await PlatformCallValueAsync(method, input, cancellationToken).ConfigureAwait(false);
        return value is bool flag
            ? flag
            : throw new BppException(BppErrorCodes.ProtocolError, $"PlatformService {method} 返回无效结果");
    }

    internal async Task PlatformCallVoidAsync(
        string method,
        object? input,
        CancellationToken cancellationToken = default)
    {
        await PlatformCallValueAsync(method, input, cancellationToken).ConfigureAwait(false);
    }

    // —— 窗口 ——

    internal async Task<WindowHandle> CreateBrowserWindowAsync(
        string url,
        WindowOptions options,
        string? scopedParentRequestId,
        CancellationToken cancellationToken)
    {
        var element = await PlatformCallRawAsync(
            "ui.window.create",
            new Dictionary<string, object?> { ["url"] = url, ["options"] = options },
            cancellationToken).ConfigureAwait(false);
        var created = element.Deserialize<WindowCreateResult>(JsonDefaults.Options)
            ?? throw new BppException(BppErrorCodes.ProtocolError, "ui.window.create returned an invalid result");
        if (string.IsNullOrEmpty(created.WindowKey) || created.WindowId == 0 || created.WebContentsId == 0)
        {
            throw new BppException(BppErrorCodes.ProtocolError, "ui.window.create returned an invalid result");
        }
        var handle = new WindowHandle(
            this,
            created.WindowKey,
            created.WindowId,
            created.WebContentsId,
            string.IsNullOrEmpty(created.Url) ? url : created.Url,
            scopedParentRequestId);
        RegisterWindow(handle);
        return handle;
    }

    internal void RegisterWindow(WindowHandle handle)
    {
        lock (_sync)
        {
            _windows[handle.ID] = handle;
        }
    }

    internal void RemoveWindow(long windowId, WindowHandle expected)
    {
        lock (_sync)
        {
            if (_windows.TryGetValue(windowId, out var current) && ReferenceEquals(current, expected))
            {
                _windows.Remove(windowId);
            }
        }
    }

    // —— 事件 ——

    internal void EnsureEventSubscription(string topic)
    {
        var client = _platformClient;
        if (client is null)
        {
            return;
        }
        lock (_sync)
        {
            if (_eventSubscriptions.ContainsKey(topic))
            {
                return;
            }
        }
        var subscription = client.Subscribe(topic, HandleDomainEvent);
        lock (_sync)
        {
            _eventSubscriptions[topic] = subscription;
        }
    }

    internal void DropEventSubscription(string topic)
    {
        if (topic.StartsWith("window.", StringComparison.Ordinal))
        {
            return;
        }
        IDisposable? subscription = null;
        lock (_sync)
        {
            if (_eventSubscriptions.TryGetValue(topic, out var existing))
            {
                subscription = existing;
                _eventSubscriptions.Remove(topic);
            }
        }
        subscription?.Dispose();
    }

    internal async Task PublishEventAsync(string eventName, object? payload, CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "EventService 未连接；gRPC Runtime 是唯一路径");
        using var link = LinkScope(cancellationToken);
        await client.PublishAsync(eventName, payload, null, link.Token).ConfigureAwait(false);
    }

    // —— 日志 ——

    internal void EmitLog(
        string level,
        string message,
        Exception? error,
        IReadOnlyDictionary<string, object?>? fields,
        string? invocationId)
    {
        var client = _platformClient;
        if (client is null)
        {
            return;
        }
        var payload = new Dictionary<string, object?>
        {
            ["level"] = level,
            ["message"] = message,
            ["fields"] = fields,
            ["invocationId"] = invocationId,
        };
        if (error is not null)
        {
            payload["error"] = error.Message;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await client.PlatformCallAsync("diagnostics.log", payload, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 平台未连接时日志 no-op。
            }
        });
    }

    // —— 内部工具 ——

    private async Task<object?> RunInScopeAsync(CommandContext context, Func<Task<object?>> action)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new CommandScope(context.RequestID, context.CancellationToken);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            CurrentScope.Value = previous;
        }
    }

    private async Task<object?> PlatformCallValueAsync(
        string method,
        object? input,
        CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "PlatformService 未连接；gRPC Runtime 是唯一路径");
        using var link = LinkScope(cancellationToken);
        return await client.PlatformCallAsync(method, input, null, link.Token).ConfigureAwait(false);
    }

    private async Task<Interaction> PlatformInteractAsync(
        string commandId,
        object? input,
        string? intent,
        CancellationToken cancellationToken)
    {
        var client = _platformClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "PlatformService 未连接；gRPC Runtime 是唯一路径");
        return await client
            .PlatformInteractAsync(commandId, input, CurrentInvocationId, intent, null, EffectiveToken(cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<ResourceHandle> CreateResourceCoreAsync(
        object content,
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var defaultMime = content switch
        {
            string => "text/plain; charset=utf-8",
            byte[] => "application/octet-stream",
            _ => throw new BppException(BppErrorCodes.InvalidInput, "资源内容必须是 string 或 byte[]。"),
        };
        var data = content switch
        {
            string text => global::System.Text.Encoding.UTF8.GetBytes(text),
            byte[] bytes => bytes,
            _ => throw new BppException(BppErrorCodes.InvalidInput, "资源内容必须是 string 或 byte[]。"),
        };
        var resolved = ResolveOptions(options, defaultMime);
        if (data.Length > RuntimeMetadata.ResourceChunkBytes)
        {
            var writer = await CreateResourceWriterCoreAsync(resolved, requestId, cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                writer.Abort();
                throw;
            }
            return await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        var client = _resourceClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "ResourceService 未就绪");
        var header = BuildCreateHeader(resolved);
        header.ExpectedSizeBytes = (ulong)data.Length;
        using var link = LinkScope(cancellationToken);
        var stream = client.BeginCreate(header, InvocationMetadata(requestId), link.Token);
        try
        {
            await stream.SendChunkAsync(data, link.Token).ConfigureAwait(false);
            var proto = await stream.FinishAsync(link.Token).ConfigureAwait(false);
            return new ResourceHandle(client, BrickValueCodec.ToSdkResourceRef(proto));
        }
        catch (Exception)
        {
            stream.Abort();
            throw;
        }
    }

    private async Task<ResourceHandle> CreateResourceFromCoreAsync(
        Stream source,
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (options is { ExpectedSizeBytes: < 0 })
        {
            throw new BppException(BppErrorCodes.InvalidInput, "ExpectedSizeBytes 必须是非负整数。");
        }
        var resolved = ResolveOptions(options, "application/octet-stream");
        var writer = await CreateResourceWriterCoreAsync(resolved, requestId, cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.ReadFromAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            writer.Abort();
            throw;
        }
        return await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResourceWriter> CreateResourceWriterCoreAsync(
        ResourceCreateOptions? options,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var client = _resourceClient
            ?? throw new BppException(BppErrorCodes.ProtocolError, "ResourceService 未就绪");
        var header = BuildCreateHeader(ResolveOptions(options, "application/octet-stream"));
        using var link = LinkScope(cancellationToken);
        var stream = client.BeginCreate(header, InvocationMetadata(requestId), link.Token);
        await Task.CompletedTask.ConfigureAwait(false);
        return new ResourceWriter(stream, client);
    }

    private static ResourceCreateHeader BuildCreateHeader(ResourceCreateOptions? options)
    {
        var header = new ResourceCreateHeader();
        if (options is null)
        {
            return header;
        }
        if (!string.IsNullOrEmpty(options.Name))
        {
            header.Name = options.Name;
        }
        if (!string.IsNullOrEmpty(options.MimeType))
        {
            header.MediaType = options.MimeType;
        }
        if (options.TtlMillis > 0)
        {
            header.TtlMs = (ulong)options.TtlMillis;
        }
        if (options.ExpectedSizeBytes > 0)
        {
            header.ExpectedSizeBytes = (ulong)options.ExpectedSizeBytes;
        }
        return header;
    }

    private static ResourceCreateOptions ResolveOptions(ResourceCreateOptions? options, string defaultMime)
    {
        if (options is null)
        {
            return new ResourceCreateOptions { MimeType = defaultMime };
        }
        return new ResourceCreateOptions
        {
            MimeType = string.IsNullOrEmpty(options.MimeType) ? defaultMime : options.MimeType,
            Name = options.Name,
            TtlMillis = options.TtlMillis,
            ExpectedSizeBytes = options.ExpectedSizeBytes,
        };
    }

    private HostBrickStorageClient StorageClientOrThrow() =>
        _storageClient
        ?? throw new BppException(BppErrorCodes.ProtocolError, "Host BrickStorageService 未就绪");

    private Metadata? InvocationMetadata(string? requestId)
    {
        var id = string.IsNullOrEmpty(requestId) ? CurrentInvocationId : requestId;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        return new Metadata { { RuntimeMetadata.InvocationId, id } };
    }

    private ScopeLink LinkScope(CancellationToken explicitToken)
    {
        var scope = CurrentScope.Value;
        if (scope is null || !scope.Token.CanBeCanceled || explicitToken == scope.Token)
        {
            return new ScopeLink(explicitToken, null);
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, explicitToken);
        return new ScopeLink(linked.Token, linked);
    }

    private CancellationToken EffectiveToken(CancellationToken explicitToken)
    {
        var scope = CurrentScope.Value;
        if (scope is null || !scope.Token.CanBeCanceled)
        {
            return explicitToken;
        }
        if (!explicitToken.CanBeCanceled || explicitToken == scope.Token)
        {
            return scope.Token;
        }
        return explicitToken;
    }

    private void AttachEventSubscriptions()
    {
        foreach (var topic in WindowHostEventTopics)
        {
            EnsureEventSubscription(topic);
        }
        foreach (var topic in Events.SubscribedTopics())
        {
            EnsureEventSubscription(topic);
        }
    }

    private void ClearEventSubscriptions()
    {
        IDisposable[] subscriptions;
        lock (_sync)
        {
            subscriptions = _eventSubscriptions.Values.ToArray();
            _eventSubscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void ClearWindows()
    {
        WindowHandle[] handles;
        lock (_sync)
        {
            handles = _windows.Values.ToArray();
            _windows.Clear();
        }
        foreach (var handle in handles)
        {
            handle.DisposeLocal(string.Empty);
        }
    }

    private void HandleDomainEvent(string topic, object? payload)
    {
        var raw = new Dictionary<string, object?> { ["event"] = topic, ["payload"] = payload };
        if (payload is IReadOnlyDictionary<string, object?> fields &&
            fields.TryGetValue("requestId", out var requestId) &&
            requestId is string requestIdText)
        {
            raw["requestId"] = requestIdText;
        }

        if (topic == "window.closed" &&
            payload is IReadOnlyDictionary<string, object?> closed &&
            closed.TryGetValue("eventId", out var eventId) &&
            eventId is string eventIdText &&
            !RememberTerminalWindowEvent(eventIdText))
        {
            return;
        }

        if (topic.StartsWith("window.", StringComparison.Ordinal))
        {
            if (payload is IReadOnlyDictionary<string, object?> map && TryGetWindowId(map, out var windowId))
            {
                WindowHandle? handle;
                lock (_sync)
                {
                    _windows.TryGetValue(windowId, out handle);
                }
                if (handle is not null)
                {
                    var name = topic["window.".Length..];
                    var payloadMap = ToDictionary(map);
                    if (name is "notify" or "request" or "request.cancel")
                    {
                        handle.DispatchChildRpc(name, payloadMap);
                    }
                    else
                    {
                        handle.Emit(name, payloadMap);
                    }
                }
            }
        }

        if (!topic.StartsWith("window.", StringComparison.Ordinal))
        {
            Events.Dispatch(topic, payload, raw);
        }
    }

    private bool RememberTerminalWindowEvent(string eventId)
    {
        lock (_sync)
        {
            if (_terminalWindowEventIds.Contains(eventId))
            {
                return false;
            }
            _terminalWindowEventIds.Add(eventId);
            _terminalWindowEventOrder.Enqueue(eventId);
            if (_terminalWindowEventOrder.Count > MaxTerminalWindowEventIds)
            {
                var oldest = _terminalWindowEventOrder.Dequeue();
                _terminalWindowEventIds.Remove(oldest);
            }
            return true;
        }
    }

    private async Task CleanupTransportAsync()
    {
        ClearEventSubscriptions();
        _resourceClient?.Dispose();
        _resourceClient = null;
        _storageClient?.Dispose();
        _storageClient = null;
        _platformClient?.Dispose();
        _platformClient = null;
        _registryChannel?.Dispose();
        _registryChannel = null;
        _registry = null;
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    private async Task RunHookAsync(Func<Task> hook, string name)
    {
        try
        {
            await hook().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            EmitLog("error", name + " error", error, null, null);
        }
    }

    private static bool TryGetWindowId(IReadOnlyDictionary<string, object?> payload, out long windowId)
    {
        windowId = 0;
        if (!payload.TryGetValue("windowId", out var raw) || raw is null)
        {
            return false;
        }
        switch (raw)
        {
            case long value:
                windowId = value;
                return true;
            case int value:
                windowId = value;
                return true;
            case double value when value == Math.Truncate(value):
                windowId = (long)value;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetInt64(out windowId);
            default:
                return false;
        }
    }

    private static Dictionary<string, object?> ToDictionary(IReadOnlyDictionary<string, object?> source)
    {
        return source is Dictionary<string, object?> dictionary
            ? dictionary
            : new Dictionary<string, object?>(source);
    }

    private sealed record CommandScope(string RequestId, CancellationToken Token);

    private readonly struct ScopeLink : IDisposable
    {
        private readonly CancellationTokenSource? _source;

        public ScopeLink(CancellationToken token, CancellationTokenSource? source)
        {
            Token = token;
            _source = source;
        }

        public CancellationToken Token { get; }

        public void Dispose() => _source?.Dispose();
    }

    private sealed class RuntimeSelfClient : IBrickClient
    {
        private readonly BricklyRuntime _runtime;

        public RuntimeSelfClient(BricklyRuntime runtime)
        {
            _runtime = runtime;
        }

        public Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default) =>
            _runtime.InvokeAsync(commandId, input, cancellationToken);

        public async Task<Interaction> InteractAsync(
            string commandId,
            object? input,
            InteractOptions options,
            CancellationToken cancellationToken = default)
        {
            InteractionSupport.RequireOnEvent(options);
            var session = await _runtime
                .PlatformInteractAsync(commandId, input, "call", cancellationToken)
                .ConfigureAwait(false);
            InteractionSupport.Pump(session, options.OnEvent);
            return session;
        }
    }

    private sealed class WindowCreateResult
    {
        public string? WindowKey { get; init; }
        public long WindowId { get; init; }
        public int WebContentsId { get; init; }
        public string? Url { get; init; }
    }
}
