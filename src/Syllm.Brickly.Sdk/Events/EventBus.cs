using System.Text.RegularExpressions;

namespace Syllm.Brickly.Sdk;

/// <summary>事件来源；显式区分系统事件与携带完整身份的 Brick 事件。</summary>
public sealed class EventSource
{
    public string Kind { get; init; } = "system";

    public BrickRef? Ref { get; init; }
}

/// <summary>事件回调的第二个参数，描述事件元信息。</summary>
public sealed class EventEnvelope
{
    public string Event { get; init; } = string.Empty;

    public object? Payload { get; init; }

    public EventSource Source { get; init; } = new();

    public string? PublishedAt { get; init; }

    public string? RequestId { get; init; }
}

/// <summary>事件订阅与发布，对应 Node SDK 的 brick.events。</summary>
public sealed class EventBus
{
    private static readonly Regex PublicEventName = new(
        "^[A-Za-z0-9_.-]+:[A-Za-z0-9_.:-]+$",
        RegexOptions.Compiled);

    private const string PublicEventNameHint =
        "公共事件名必须是「命名空间:主题」，例如 clipboard:new-content、my-brick:tick。只允许字母、数字、_ . - :，中间必须有冒号。";

    private readonly BricklyRuntime _runtime;
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<ulong, Action<object?, EventEnvelope>>> _subs = new();
    private ulong _counter;

    internal EventBus(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// 订阅公共事件，返回 IDisposable（幂等）。名字必须是「命名空间:主题」；
    /// window.* 只走 WindowHandle.On。
    /// </summary>
    public IDisposable On(string eventName, Action<object?, EventEnvelope> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RequirePublicEventName(eventName);

        var id = ++_counter;
        lock (_sync)
        {
            if (!_subs.TryGetValue(eventName, out var map))
            {
                map = new Dictionary<ulong, Action<object?, EventEnvelope>>();
                _subs[eventName] = map;
            }
            map[id] = handler;
        }
        _runtime.EnsureEventSubscription(eventName);

        return new Subscription(() =>
        {
            var empty = false;
            lock (_sync)
            {
                if (_subs.TryGetValue(eventName, out var map) && map.Remove(id) && map.Count == 0)
                {
                    _subs.Remove(eventName);
                    empty = true;
                }
            }
            if (empty)
            {
                _runtime.DropEventSubscription(eventName);
            }
        });
    }

    /// <summary>发布事件到事件总线（走 Host EventService）。</summary>
    public async Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
    {
        RequirePublicEventName(eventName);
        await _runtime.PublishEventAsync(eventName, payload, cancellationToken).ConfigureAwait(false);
    }

    internal void Dispatch(string eventName, object? payload, IReadOnlyDictionary<string, object?> raw)
    {
        Action<object?, EventEnvelope>[] handlers;
        lock (_sync)
        {
            if (!_subs.TryGetValue(eventName, out var map) || map.Count == 0)
            {
                return;
            }
            handlers = map.Values.ToArray();
        }

        var envelope = new EventEnvelope
        {
            Event = eventName,
            Payload = payload,
            RequestId = raw.TryGetValue("requestId", out var requestId) ? requestId as string : null,
            PublishedAt = raw.TryGetValue("publishedAt", out var publishedAt) ? publishedAt as string : null,
        };

        if (raw.TryGetValue("source", out var sourceValue) &&
            sourceValue is IReadOnlyDictionary<string, object?> source)
        {
            var kind = source.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
            BrickRef? reference = null;
            if (source.TryGetValue("ref", out var refValue) &&
                refValue is IReadOnlyDictionary<string, object?> refMap &&
                refMap.TryGetValue("brickId", out var brickId) &&
                brickId is string brickIdText &&
                refMap.TryGetValue("version", out var version) &&
                version is string versionText &&
                refMap.TryGetValue("origin", out var origin) &&
                origin is string originText &&
                BrickOrigins.TryParse(originText, out var parsedOrigin))
            {
                reference = new BrickRef(brickIdText, parsedOrigin, versionText);
            }
            envelope = new EventEnvelope
            {
                Event = eventName,
                Payload = payload,
                RequestId = envelope.RequestId,
                PublishedAt = envelope.PublishedAt,
                Source = new EventSource { Kind = kind ?? "system", Ref = reference },
            };
        }

        foreach (var handler in handlers)
        {
            var captured = handler;
            _ = Task.Run(() =>
            {
                try
                {
                    captured(payload, envelope);
                }
                catch (Exception)
                {
                    // 订阅者异常不传播。
                }
            });
        }
    }

    internal IReadOnlyList<string> SubscribedTopics()
    {
        lock (_sync)
        {
            return _subs.Keys.ToArray();
        }
    }

    internal static void RequirePublicEventName(string eventName)
    {
        if (!PublicEventName.IsMatch(eventName))
        {
            throw new BppException(BppErrorCodes.InvalidInput, $"无效的事件名：{eventName}。{PublicEventNameHint}");
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
