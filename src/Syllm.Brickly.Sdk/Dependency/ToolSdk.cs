namespace Syllm.Brickly.Sdk;

/// <summary>公开 Handle 状态。</summary>
public enum ToolHandleState
{
    Active,
    Disposed,
    Stopping,
    Stopped,
    Failed,
}

/// <summary>控制显式 start。</summary>
public sealed class StartToolOptions
{
    public string? ProfileId { get; init; }

    /// <summary>Owner 取消等价于 DisposeAsync，不会 Stop。</summary>
    public CancellationToken Owner { get; init; }
}

/// <summary>宿主 Lifetime 端口；测试可注入。</summary>
public interface IToolLifetimeHost
{
    Task<string> StartAsync(BrickRef tool, StartToolOptions options, CancellationToken cancellationToken);

    Task<object?> InvokeAsync(string handleId, string commandId, object? input, CancellationToken cancellationToken);

    Task<Interaction> InteractAsync(
        string handleId,
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken);

    Task DisposeAsync(string handleId, CancellationToken cancellationToken);

    Task StopAsync(string handleId, CancellationToken cancellationToken);

    Task<object?> InvokeOnceAsync(BrickRef tool, string commandId, object? input, CancellationToken cancellationToken);

    Task<Interaction> InteractOnceAsync(
        BrickRef tool,
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken);
}

/// <summary>调用方持有的长期所有权。不暴露 lifetimeId / retainer。</summary>
public sealed class ToolHandle
{
    private readonly string _id;
    private readonly IToolLifetimeHost _host;

    internal ToolHandle(string id, IToolLifetimeHost host)
    {
        _id = id;
        _host = host;
        State = ToolHandleState.Active;
    }

    public ToolHandleState State { get; private set; }

    public async Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default)
    {
        AssertCallable();
        return await _host.InvokeAsync(_id, commandId, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Interaction> InteractAsync(
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default)
    {
        AssertCallable();
        InteractionSupport.RequireOnEvent(options);
        var session = await _host
            .InteractAsync(_id, commandId, input, options, cancellationToken)
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
        AssertCallable();
        InteractionSupport.RequireOnEvent(options);
        return InteractionSupport.CallAsync(new ToolHandleClient(this), commandId, input, options, cancellationToken);
    }

    /// <summary>释放 Handle 所有权，不取消在途 Call。</summary>
    public async Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        if (State == ToolHandleState.Disposed)
        {
            return;
        }
        await _host.DisposeAsync(_id, cancellationToken).ConfigureAwait(false);
        if (State != ToolHandleState.Failed)
        {
            State = ToolHandleState.Disposed;
        }
    }

    /// <summary>DisposeAsync 的别名，无 force 参数。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) => DisposeAsync(cancellationToken);

    /// <summary>取消该 Lifetime 的调用并关闭全部窗口。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State is ToolHandleState.Stopped or ToolHandleState.Failed)
        {
            return;
        }
        State = ToolHandleState.Stopping;
        await _host.StopAsync(_id, cancellationToken).ConfigureAwait(false);
        if (State is not ToolHandleState.Failed and not ToolHandleState.Disposed)
        {
            State = ToolHandleState.Stopped;
        }
    }

    private void AssertCallable()
    {
        if (State == ToolHandleState.Failed)
        {
            throw new BppException(BppErrorCodes.RuntimeUnavailable, "Runtime 已失败，Handle 不可用");
        }
        if (State != ToolHandleState.Active)
        {
            throw new BppException(BppErrorCodes.HandleClosed, "ToolHandle 已关闭，不能发起新调用");
        }
    }

    private sealed class ToolHandleClient : IBrickClient
    {
        private readonly ToolHandle _handle;

        public ToolHandleClient(ToolHandle handle)
        {
            _handle = handle;
        }

        public Task<object?> InvokeAsync(string commandId, object? input, CancellationToken cancellationToken = default) =>
            _handle.InvokeAsync(commandId, input, cancellationToken);

        public Task<Interaction> InteractAsync(
            string commandId,
            object? input,
            InteractOptions options,
            CancellationToken cancellationToken = default) =>
            _handle.InteractAsync(commandId, input, options, cancellationToken);
    }
}

/// <summary>只暴露 start / invoke / interact 的公开工具 SDK。</summary>
public sealed class ToolSdk
{
    private readonly IToolLifetimeHost _host;

    public ToolSdk(IToolLifetimeHost host)
    {
        _host = host;
    }

    /// <summary>Start 成功表示 Runtime Ready。</summary>
    public async Task<ToolHandle> StartAsync(
        BrickRef tool,
        StartToolOptions options,
        CancellationToken cancellationToken = default)
    {
        var id = await _host.StartAsync(tool, options, cancellationToken).ConfigureAwait(false);
        var handle = new ToolHandle(id, _host);
        if (options.Owner.CanBeCanceled)
        {
            var owner = options.Owner;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, owner).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                await handle.DisposeAsync(CancellationToken.None).ConfigureAwait(false);
            });
        }
        return handle;
    }

    /// <summary>无 Handle 的一次性调用。</summary>
    public Task<object?> InvokeAsync(
        BrickRef tool,
        string commandId,
        object? input,
        CancellationToken cancellationToken = default) =>
        _host.InvokeOnceAsync(tool, commandId, input, cancellationToken);

    /// <summary>无 Handle 的一次性交互会话；必须传入 OnEvent。</summary>
    public async Task<Interaction> InteractAsync(
        BrickRef tool,
        string commandId,
        object? input,
        InteractOptions options,
        CancellationToken cancellationToken = default)
    {
        InteractionSupport.RequireOnEvent(options);
        var session = await _host
            .InteractOnceAsync(tool, commandId, input, options, cancellationToken)
            .ConfigureAwait(false);
        InteractionSupport.Pump(session, options.OnEvent);
        return session;
    }
}
