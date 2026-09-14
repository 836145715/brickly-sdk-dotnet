using Brickly.Runtime.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Runtime → Host PlatformService / EventService / BrickConnectorService 客户端。</summary>
internal sealed class HostPlatformClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly PlatformService.PlatformServiceClient _platform;
    private readonly EventService.EventServiceClient _events;
    private readonly BrickConnectorService.BrickConnectorServiceClient _connector;
    private readonly string _token;

    public HostPlatformClient(string endpoint, string runtimeToHostToken)
    {
        _channel = ChannelFactory.Create(endpoint, RuntimeMetadata.InvokeMaxBytes);
        _platform = new PlatformService.PlatformServiceClient(_channel);
        _events = new EventService.EventServiceClient(_channel);
        _connector = new BrickConnectorService.BrickConnectorServiceClient(_channel);
        _token = runtimeToHostToken;
    }

    public async Task<object?> PlatformCallAsync(
        string method,
        object? input,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var response = await PlatformCallRawAsync(method, input, extraMetadata, cancellationToken).ConfigureAwait(false);
        return BrickValueCodec.ToClr(response);
    }

    public async Task<BrickValue> PlatformCallRawAsync(
        string method,
        object? input,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var request = new PlatformCallRequest
        {
            Method = method,
            Input = BrickValueCodec.FromClr(input),
        };
        var response = await _platform
            .CallAsync(request, Auth(extraMetadata), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Result;
    }

    public IDisposable Subscribe(string topic, Action<string, object?> onEvent)
    {
        var cts = new CancellationTokenSource();
        var call = _events.Subscribe(
            new SubscribeEventsRequest { Topic = topic },
            Auth(null),
            cancellationToken: cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await call.ResponseStream.MoveNext(cts.Token).ConfigureAwait(false))
                {
                    var current = call.ResponseStream.Current;
                    onEvent(current.Topic, BrickValueCodec.ToClr(current.Payload));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RpcException)
            {
            }
        });
        return new EventSubscription(cts, call);
    }

    public async Task PublishAsync(
        string topic,
        object? payload,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var request = new PublishEventRequest
        {
            Topic = topic,
            Payload = BrickValueCodec.FromClr(payload),
        };
        await _events
            .PublishAsync(request, Auth(extraMetadata), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<object?> ConnectAsync(
        string brickId,
        string commandId,
        object? input,
        string? invocationId,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(extraMetadata);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        var request = new ConnectorInvokeRequest
        {
            BrickId = brickId,
            CommandId = commandId,
            Input = BrickValueCodec.FromClr(input),
            HandleId = string.Empty,
        };
        var response = await _connector
            .InvokeAsync(request, metadata, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return BrickValueCodec.ToClr(response.Result);
    }

    public async Task<object?> ConnectOnHandleAsync(
        string brickId,
        string commandId,
        object? input,
        string? invocationId,
        string handleId,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(extraMetadata);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        var request = new ConnectorInvokeRequest
        {
            BrickId = brickId,
            CommandId = commandId,
            Input = BrickValueCodec.FromClr(input),
            HandleId = handleId,
        };
        var response = await _connector
            .InvokeAsync(request, metadata, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return BrickValueCodec.ToClr(response.Result);
    }

    public async Task<string> StartDependencyAsync(
        string brickId,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(null);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        var response = await _connector
            .StartAsync(new ConnectorStartRequest { BrickId = brickId }, metadata, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.HandleId;
    }

    public async Task DisposeDependencyAsync(
        string handleId,
        string invocationId,
        bool stop,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(null);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        var request = new ConnectorDisposeRequest { HandleId = handleId, Stop = stop };
        await _connector.DisposeAsync(request, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectorInteraction> PlatformInteractAsync(
        string commandId,
        object? input,
        string? invocationId,
        string? intent,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(extraMetadata);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        if (intent == "call")
        {
            metadata.Add(RuntimeMetadata.Intent, "call");
        }
        return await StartInteractionAsync(
            callToken => _platform.Interact(metadata, cancellationToken: callToken),
            commandId,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectorInteraction> InteractAsync(
        string brickId,
        string commandId,
        object? input,
        string? invocationId,
        string? handleId,
        string? intent,
        Metadata? extraMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = Auth(extraMetadata);
        metadata.Add(RuntimeMetadata.TargetBrickId, brickId);
        if (!string.IsNullOrEmpty(invocationId))
        {
            metadata.Add(RuntimeMetadata.InvocationId, invocationId);
        }
        if (!string.IsNullOrEmpty(handleId))
        {
            metadata.Add(RuntimeMetadata.HandleId, handleId);
        }
        if (intent == "call")
        {
            metadata.Add(RuntimeMetadata.Intent, "call");
        }
        return await StartInteractionAsync(
            callToken => _connector.Interact(metadata, cancellationToken: callToken),
            commandId,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _channel.Dispose();

    private static async Task<ConnectorInteraction> StartInteractionAsync(
        Func<CancellationToken, AsyncDuplexStreamingCall<ClientFrame, ServerFrame>> createCall,
        string commandId,
        object? input,
        CancellationToken cancellationToken)
    {
        var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var call = createCall(callCts.Token);
        var session = new ConnectorInteraction(call, callCts);
        try
        {
            await session.OpenAsync(commandId, BrickValueCodec.FromClr(input)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            session.Dispose();
            throw;
        }
        session.StartReadLoop();
        return session;
    }

    private Metadata Auth(Metadata? extra)
    {
        var metadata = new Metadata { { RuntimeMetadata.RuntimeToken, _token } };
        if (extra is not null)
        {
            foreach (var entry in extra)
            {
                metadata.Add(entry);
            }
        }
        return metadata;
    }

    private sealed class EventSubscription : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly AsyncServerStreamingCall<DomainEvent> _call;

        public EventSubscription(CancellationTokenSource cts, AsyncServerStreamingCall<DomainEvent> call)
        {
            _cts = cts;
            _call = call;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _call.Dispose();
            _cts.Dispose();
        }
    }
}
