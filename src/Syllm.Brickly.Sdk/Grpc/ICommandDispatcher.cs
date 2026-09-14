using System.Text.Json;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>Runtime 侧命令分发面；由 BricklyRuntime 实现，供 gRPC 服务端回调。</summary>
internal interface ICommandDispatcher
{
    IReadOnlyList<string> CommandIds { get; }

    Task<object?> DispatchInvokeAsync(
        string commandId,
        JsonElement input,
        string? invocationId,
        CancellationToken cancellationToken);

    Task<object?> DispatchInteractAsync(string commandId, InteractServerSession session);
}
