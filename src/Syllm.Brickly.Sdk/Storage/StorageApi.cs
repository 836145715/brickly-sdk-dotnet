namespace Syllm.Brickly.Sdk;

/// <summary>按 Brick 隔离的本机持久存储，与 Node 的 brick.storage / ctx.storage 对齐。</summary>
public sealed class StorageApi
{
    private readonly BricklyRuntime _runtime;

    internal StorageApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
        KV = new KVStore(runtime, "user");
        Secrets = new KVStore(runtime, "secret");
    }

    /// <summary>默认 user 作用域键值面。</summary>
    public KVStore KV { get; }

    /// <summary>secret 作用域键值面。</summary>
    public KVStore Secrets { get; }

    /// <summary>返回命名文档集合；scope 默认 user。</summary>
    public Collection Collection(string name, string scope = "user")
    {
        return new Collection(_runtime, name, string.IsNullOrEmpty(scope) ? "user" : scope);
    }

    /// <summary>返回当前生效配额与占用。</summary>
    public Task<Dictionary<string, object?>> StatusAsync(CancellationToken cancellationToken = default) =>
        _runtime.StorageStatusAsync(cancellationToken);
}

/// <summary>默认 user 或 secrets 的键值面。</summary>
public sealed class KVStore
{
    private readonly BricklyRuntime _runtime;
    private readonly string _scope;

    internal KVStore(BricklyRuntime runtime, string scope)
    {
        _runtime = runtime;
        _scope = scope;
    }

    /// <summary>读取键；不存在返回 null。</summary>
    public Task<object?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _runtime.StorageKvGetAsync(_scope, key, cancellationToken);

    public Task SetAsync(string key, object? value, CancellationToken cancellationToken = default) =>
        _runtime.StorageKvSetAsync(_scope, key, value, cancellationToken);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _runtime.StorageKvDeleteAsync(_scope, key, cancellationToken);

    public Task<bool> HasAsync(string key, CancellationToken cancellationToken = default) =>
        _runtime.StorageKvHasAsync(_scope, key, cancellationToken);

    public Task<List<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) =>
        _runtime.StorageKvListAsync(_scope, prefix, cancellationToken);
}

/// <summary>弱查询文档集合。</summary>
public sealed class Collection
{
    private readonly BricklyRuntime _runtime;
    private readonly string _name;
    private readonly string _scope;

    internal Collection(BricklyRuntime runtime, string name, string scope)
    {
        _runtime = runtime;
        _name = name;
        _scope = scope;
    }

    public Task<Dictionary<string, object?>?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _runtime.StorageGetDocAsync(_scope, _name, id, cancellationToken);

    public Task<Dictionary<string, object?>> CreateAsync(
        IReadOnlyDictionary<string, object?> data,
        CancellationToken cancellationToken = default) =>
        _runtime.StorageCreateDocAsync(_scope, _name, data, cancellationToken);

    public Task<Dictionary<string, object?>> PutAsync(
        IReadOnlyDictionary<string, object?> doc,
        CancellationToken cancellationToken = default) =>
        _runtime.StoragePutDocAsync(_scope, _name, doc, cancellationToken);

    public Task<Dictionary<string, object?>> UpdateAsync(
        string id,
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken = default) =>
        _runtime.StorageUpdateDocAsync(_scope, _name, id, patch, cancellationToken);

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _runtime.StorageDeleteDocAsync(_scope, _name, id, cancellationToken);

    public Task<List<Dictionary<string, object?>>> ListAsync(
        IReadOnlyDictionary<string, object?>? query = null,
        CancellationToken cancellationToken = default) =>
        _runtime.StorageListDocsAsync(
            _scope,
            _name,
            query ?? new Dictionary<string, object?>(),
            cancellationToken);

    /// <summary>订阅集合变更；Dispose 取消。</summary>
    public Task<IDisposable> WatchAsync(
        Action<Dictionary<string, object?>> handler,
        CancellationToken cancellationToken = default) =>
        _runtime.StorageWatchAsync(_scope, _name, handler, cancellationToken);
}
