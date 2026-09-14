namespace Syllm.Brickly.Sdk;

/// <summary>InvokeAsync 可选参数。</summary>
public sealed class InvokeOptions
{
    /// <summary>指定目标 Brick 的 Profile ID；显式 Profile 始终优先。</summary>
    public string? ProfileId { get; init; }
}

/// <summary>一个 manifest alias 对应的精确目标客户端。</summary>
public sealed class DependencyClient
{
    private readonly BricklyRuntime _runtime;
    private readonly string? _parentRequestId;
    private readonly TraceContext? _trace;
    private readonly IReadOnlyDictionary<string, string>? _dependencyProfiles;

    internal DependencyClient(
        BricklyRuntime runtime,
        string alias,
        BrickRef reference,
        string? parentRequestId,
        TraceContext? trace,
        IReadOnlyDictionary<string, string>? dependencyProfiles)
    {
        _runtime = runtime;
        Alias = alias;
        Ref = reference;
        _parentRequestId = parentRequestId;
        _trace = trace;
        _dependencyProfiles = dependencyProfiles;
    }

    /// <summary>manifest 中声明的依赖别名。</summary>
    public string Alias { get; }

    /// <summary>Host 绑定的精确目标副本。</summary>
    public BrickRef Ref { get; }

    /// <summary>调用依赖命令；有当前命令则挂为 child，否则是 root。</summary>
    public Task<object?> InvokeAsync(
        string commandId,
        object? input,
        InvokeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 与 Node / Go 一致：Profile 只参与解析（无对应 metadata key），不写入线上请求。
        _ = options?.ProfileId ?? _dependencyProfiles?.GetValueOrDefault(BrickRef.BrickKeyOf(Ref));
        return _runtime.ConnectorInvokeAsync(Ref, commandId, input, ParentId(), null, cancellationToken);
    }

    /// <summary>对依赖开一条双工会话；没有当前命令时是 root。</summary>
    public async Task<Interaction> InteractAsync(
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        var session = await _runtime
            .ConnectorInteractAsync(Ref, commandId, input, ParentId(), null, null, cancellationToken)
            .ConfigureAwait(false);
        InteractionSupport.Pump(session, options.OnEvent);
        return session;
    }

    /// <summary>CallAsync = Interact + 半关闭；必须与命令 mode=call 对齐。</summary>
    public Task<object?> CallAsync(
        string commandId,
        object? input,
        CallOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        return InteractionSupport.CallAsync(
            new DependencyBrickClient(this),
            commandId,
            input,
            options,
            cancellationToken);
    }

    /// <summary>命令内占用目标；跟这次 Call，return/cancel 自动放手。命令外请先 InvokeAsync。</summary>
    public async Task<StartedToolHandle> StartAsync() =>
        await _runtime.StartDependencyAsync(Alias, Ref).ConfigureAwait(false);

    internal string? ParentId() =>
        !string.IsNullOrEmpty(_parentRequestId) ? _parentRequestId : _runtime.CurrentInvocationId;

    private sealed class DependencyBrickClient : IBrickClient
    {
        private readonly DependencyClient _dependency;

        public DependencyBrickClient(DependencyClient dependency)
        {
            _dependency = dependency;
        }

        public Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default) =>
            _dependency.InvokeAsync(commandId, input, null, cancellationToken);

        public async Task<Interaction> InteractAsync(
            string commandId,
            object? input,
            InteractOptions options,
            CancellationToken cancellationToken = default)
        {
            InteractionSupport.RequireOnEvent(options);
            var session = await _dependency._runtime
                .ConnectorInteractAsync(
                    _dependency.Ref,
                    commandId,
                    input,
                    _dependency.ParentId(),
                    null,
                    "call",
                    cancellationToken)
                .ConfigureAwait(false);
            InteractionSupport.Pump(session, options.OnEvent);
            return session;
        }
    }
}

/// <summary>命令内 start 得到的占用；跟这次 Call，return 自动放手。</summary>
public sealed class StartedToolHandle
{
    private readonly BricklyRuntime _runtime;

    internal StartedToolHandle(BricklyRuntime runtime, BrickRef reference, string handleId, string invocationId)
    {
        _runtime = runtime;
        Ref = reference;
        HandleId = handleId;
        InvocationId = invocationId;
    }

    public BrickRef Ref { get; }

    internal string HandleId { get; }

    internal string InvocationId { get; }

    public Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default) =>
        _runtime.ConnectorInvokeAsync(Ref, commandId, input, InvocationId, HandleId, cancellationToken);

    public async Task<Interaction> InteractAsync(
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        var session = await _runtime
            .ConnectorInteractAsync(Ref, commandId, input, InvocationId, HandleId, null, cancellationToken)
            .ConfigureAwait(false);
        InteractionSupport.Pump(session, options.OnEvent);
        return session;
    }

    public Task<object?> CallAsync(
        string commandId,
        object? input,
        CallOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        return InteractionSupport.CallAsync(new StartedHandleClient(this), commandId, input, options, cancellationToken);
    }

    /// <summary>释放 Handle 所有权，不取消在途 Call。</summary>
    public Task DisposeAsync(CancellationToken cancellationToken = default) =>
        _runtime.DisposeStartedAsync(HandleId, InvocationId, stop: false, cancellationToken);

    /// <summary>DisposeAsync 的别名。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) => DisposeAsync(cancellationToken);

    /// <summary>取消该 Lifetime 的调用并关闭全部窗口。</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _runtime.DisposeStartedAsync(HandleId, InvocationId, stop: true, cancellationToken);

    private sealed class StartedHandleClient : IBrickClient
    {
        private readonly StartedToolHandle _handle;

        public StartedHandleClient(StartedToolHandle handle)
        {
            _handle = handle;
        }

        public Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default) =>
            _handle.InvokeAsync(commandId, input, cancellationToken);

        public async Task<Interaction> InteractAsync(
            string commandId,
            object? input,
            InteractOptions options,
            CancellationToken cancellationToken = default)
        {
            InteractionSupport.RequireOnEvent(options);
            var session = await _handle._runtime
                .ConnectorInteractAsync(
                    _handle.Ref,
                    commandId,
                    input,
                    _handle.InvocationId,
                    _handle.HandleId,
                    "call",
                    cancellationToken)
                .ConfigureAwait(false);
            InteractionSupport.Pump(session, options.OnEvent);
            return session;
        }
    }
}
