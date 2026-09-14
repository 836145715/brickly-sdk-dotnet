using Brickly.Runtime.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Syllm.Brickly.Sdk.Grpc;

/// <summary>
/// BrickError 与 gRPC status 的双向映射。稳定 code → status 表与 specs/runtime/v1/error.proto 注释一致。
/// </summary>
internal static class BrickErrorStatus
{
    public const string BrickErrorTypeUrl = "type.googleapis.com/brickly.runtime.v1.BrickError";

    private const string StatusDetailsKey = "grpc-status-details-bin";

    public static StatusCode MapCode(string brickCode) => brickCode switch
    {
        "INVALID_INPUT" or "INVALID_RESOURCE_REF" or "RESOURCE_OFFSET_MISMATCH" or "STORAGE_INVALID_KEY" => StatusCode.InvalidArgument,
        "ACCESS_DENIED" or "DEPENDENCY_NOT_DECLARED" or "DEPENDENCY_COMMAND_NOT_ALLOWED" or
            "DEPENDENCY_TARGET_MISMATCH" or "RESOURCE_ACCESS_DENIED" => StatusCode.PermissionDenied,
        "NOT_FOUND" or "RESOURCE_NOT_FOUND" or "STORAGE_NOT_FOUND" => StatusCode.NotFound,
        "CONFLICT" or "STORAGE_CONFLICT" => StatusCode.Aborted,
        "LIMIT_EXCEEDED" or "RESOURCE_QUOTA_EXCEEDED" or "STORAGE_VALUE_TOO_LARGE" or
            "STORAGE_QUOTA_EXCEEDED" => StatusCode.ResourceExhausted,
        "DEADLINE_EXCEEDED" => StatusCode.DeadlineExceeded,
        "CANCELLED" => StatusCode.Cancelled,
        "OUTCOME_UNKNOWN" => StatusCode.Unavailable,
        "CALL_CYCLE_DETECTED" or "REQUEST_HANDLER_UNAVAILABLE" or "RESOURCE_EXPIRED" or
            "RESOURCE_REVOKED" or "STORAGE_UNAVAILABLE" => StatusCode.FailedPrecondition,
        "RESOURCE_INTEGRITY_FAILED" => StatusCode.DataLoss,
        "PROTOCOL_VIOLATION" or "INTERNAL" => StatusCode.Internal,
        _ => StatusCode.Internal,
    };

    /// <summary>把任意业务异常映射成带 BrickError details 的 RpcException。</summary>
    public static RpcException ToRpcException(Exception error)
    {
        if (error is RpcException rpcException)
        {
            return rpcException;
        }

        var code = error is BppException bpp ? bpp.Code : "INTERNAL";
        return ToRpcException(code, error.Message, retryable: false);
    }

    public static RpcException ToRpcException(string code, string message, bool retryable = false)
    {
        var mapped = MapCode(code);
        var sanitized = Sanitize(message);
        var brickError = new BrickError
        {
            Code = code,
            Message = sanitized,
            Retryable = retryable,
        };
        if (code == "INVALID_INPUT")
        {
            brickError.Details = Any.Pack(new InvalidInputDetail { Field = "input", Reason = "invalid" });
        }

        var rpcStatus = new Google.Rpc.Status
        {
            Code = (int)mapped,
            Message = sanitized,
        };
        rpcStatus.Details.Add(Any.Pack(brickError));

        var trailers = new Metadata
        {
            { StatusDetailsKey, rpcStatus.ToByteArray() },
        };
        return new RpcException(new global::Grpc.Core.Status(mapped, sanitized), trailers);
    }

    /// <summary>从 RpcException 还原 BrickError；缺失时按 status code 生成通用 BppException。</summary>
    public static BppException ToBppException(RpcException error)
    {
        var brick = TryReadBrickError(error);
        if (brick is not null)
        {
            return new BppException(brick.Code, brick.Message);
        }
        return new BppException(CodeForStatus(error.StatusCode), error.Status.Detail);
    }

    public static BrickError? TryReadBrickError(RpcException error)
    {
        var entry = error.Trailers?.FirstOrDefault(item => item.Key == StatusDetailsKey);
        if (entry is null)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = entry.ValueBytes;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        try
        {
            var rpcStatus = Google.Rpc.Status.Parser.ParseFrom(bytes);
            foreach (var detail in rpcStatus.Details)
            {
                if (detail.Is(BrickError.Descriptor))
                {
                    return detail.Unpack<BrickError>();
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
        return null;
    }

    public static string Sanitize(string message)
    {
        return message.Contains("token", StringComparison.Ordinal)
            ? message.Replace("token", "***", StringComparison.Ordinal)
            : message;
    }

    private static string CodeForStatus(StatusCode status) => status switch
    {
        StatusCode.InvalidArgument => "INVALID_INPUT",
        StatusCode.PermissionDenied => "ACCESS_DENIED",
        StatusCode.NotFound => "NOT_FOUND",
        StatusCode.Aborted => "CONFLICT",
        StatusCode.ResourceExhausted => "LIMIT_EXCEEDED",
        StatusCode.DeadlineExceeded => "DEADLINE_EXCEEDED",
        StatusCode.Cancelled => "CANCELLED",
        StatusCode.Unavailable => "OUTCOME_UNKNOWN",
        StatusCode.FailedPrecondition => "STORAGE_UNAVAILABLE",
        StatusCode.DataLoss => "RESOURCE_INTEGRITY_FAILED",
        StatusCode.Unimplemented => "PROTOCOL_VIOLATION",
        _ => "INTERNAL",
    };
}
