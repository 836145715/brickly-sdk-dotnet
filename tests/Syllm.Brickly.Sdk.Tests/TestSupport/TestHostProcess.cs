using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>
/// 真宿主测试工件进程包装：spawn @syllm/brickly-test-host 的 dist/host.cjs，
/// 解析 stdout 就绪 JSON，提供控制面 HTTP 客户端。
/// bundle 查找：BRICKLY_TEST_HOST_BUNDLE 环境变量优先，否则从输出目录向上
/// 找 node_modules/@syllm/brickly-test-host/dist/host.cjs（monorepo 命中根
/// workspace 链接；独立仓库命中本仓 node_modules）。
/// </summary>
internal sealed class TestHostProcess : IDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http = new();

    public string DataEndpoint { get; private set; } = string.Empty;
    public string ControlEndpoint { get; private set; } = string.Empty;
    public string RuntimeToHostToken { get; private set; } = string.Empty;

    private TestHostProcess(Process process)
    {
        _process = process;
    }

    /// <summary>宿主不可用（无 node 或未装工件）时返回 null，调用方应跳过用例。</summary>
    public static TestHostProcess? TryStart()
    {
        var bundle = Environment.GetEnvironmentVariable("BRICKLY_TEST_HOST_BUNDLE") ?? FindBundle();
        if (bundle is null || !File.Exists(bundle))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo("node", bundle)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("node 启动失败");
        }
        catch
        {
            return null;
        }

        var host = new TestHostProcess(process);
        // stderr 必须持续排空：管道缓冲写满会卡死宿主子进程。
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { })
                {
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });
        try
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
            var completed = Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(15))).GetAwaiter().GetResult();
            if (completed != lineTask)
            {
                throw new InvalidOperationException("等待宿主就绪行超时");
            }
            var info = JsonSerializer.Deserialize<ReadyInfo>(lineTask.Result ?? string.Empty)
                ?? throw new InvalidOperationException("就绪行解析失败");
            if (string.IsNullOrEmpty(info.DataEndpoint) || string.IsNullOrEmpty(info.ControlEndpoint))
            {
                throw new InvalidOperationException($"就绪行缺 endpoint: {lineTask.Result}");
            }
            host.DataEndpoint = info.DataEndpoint;
            host.ControlEndpoint = info.ControlEndpoint;
            host.RuntimeToHostToken = info.RuntimeToHostToken ?? string.Empty;
            host._http.BaseAddress = new Uri($"http://{host.ControlEndpoint}");
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static string? FindBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "node_modules", "@syllm", "brickly-test-host", "dist", "host.cjs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public async Task SetFaultAsync(string path, string brickCode, int count = 1)
    {
        var response = await _http.PostAsJsonAsync(
            "/faults", new { path, brickCode, count }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetAsync()
    {
        var response = await _http.PostAsync("/reset", new StringContent("{}")).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<RecordedCall>> CallsAsync()
    {
        var body = await _http.GetFromJsonAsync<CallsResponse>("/calls").ConfigureAwait(false);
        return body?.Calls ?? [];
    }

    // —— host→runtime 驱动面（0.12.0 起） ——

    /// <summary>铸一套 spawn 凭据；测试用它注入环境变量启动真 Runtime。</summary>
    public async Task<SpawnInfo> SpawnAsync(
        string brickId,
        string? instanceId = null,
        string? origin = null,
        string? version = null)
    {
        var info = await PostAsync<SpawnInfo>(
            "/spawn", new { brickId, instanceId, origin, version }).ConfigureAwait(false);
        if (string.IsNullOrEmpty(info.SpawnId) || string.IsNullOrEmpty(info.HostToRuntimeToken))
        {
            throw new InvalidOperationException($"spawn 响应缺字段: {JsonSerializer.Serialize(info)}");
        }
        return info;
    }

    /// <summary>经控制面反向 invoke runtime 命令；ok=false 时 error 含 brickCode/message。</summary>
    public Task<DriveReply> InvokeAsync(
        string? spawnId,
        string commandId,
        object? input,
        int timeoutMs = 8000,
        string? invocationId = null) =>
        PostAsync<DriveReply>("/invoke", new { spawnId, commandId, input, timeoutMs, invocationId });

    /// <summary>批量 interact：开会话、关输入、收完事件与最终结果返回。</summary>
    public Task<DriveReply> InteractAsync(
        string? spawnId,
        string commandId,
        object? input,
        int timeoutMs = 8000,
        string? invocationId = null) =>
        PostAsync<DriveReply>("/interact", new { spawnId, commandId, input, timeoutMs, invocationId });

    /// <summary>接通依赖调用内核：connector start/handle 调用路由到已注册 runtime。</summary>
    public async Task EnableDependencyKernelAsync(params string[] commands)
    {
        _ = await PostAsync<JsonElement>(
            "/kernel/dependency", new { commands }).ConfigureAwait(false);
    }

    /// <summary>往指定 runtime 的事件订阅流推一条 domain event。</summary>
    public async Task PushEventAsync(string? spawnId, string topic, object? payload)
    {
        _ = await PostAsync<JsonElement>("/events/push", new { spawnId, topic, payload }).ConfigureAwait(false);
    }

    /// <summary>给 ui.* 平台调用装罐头响应。</summary>
    public async Task SetUiResponseAsync(string method, object? result)
    {
        _ = await PostAsync<JsonElement>("/ui-handler", new { method, result }).ConfigureAwait(false);
    }

    /// <summary>已注册的 runtime handle 列表。</summary>
    public Task<JsonElement> RuntimesAsync() => GetAsync<JsonElement>("/runtimes");

    /// <summary>控制面录制到的 UI 平台调用。</summary>
    public async Task<IReadOnlyList<UiCallRecord>> UiCallsAsync()
    {
        var body = await GetAsync<UiCallsResponse>("/ui-calls").ConfigureAwait(false);
        return body?.Calls ?? [];
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var response = await _http.PostAsJsonAsync(path, body).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"控制面 {path} 返回 {(int)response.StatusCode}: {payload}");
        }
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidOperationException($"控制面 {path} 响应解析失败: {payload}");
    }

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await _http.GetAsync(path).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"控制面 {path} 返回 {(int)response.StatusCode}: {payload}");
        }
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidOperationException($"控制面 {path} 响应解析失败: {payload}");
    }

    public void Dispose()
    {
        try { _process.StandardInput.Close(); } catch { }
        if (!_process.WaitForExit(5000))
        {
            try { _process.Kill(); } catch { }
            _process.WaitForExit();
        }
        _process.Dispose();
        _http.Dispose();
    }

    public sealed record RecordedCall(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("at")] long At);

    public sealed record SpawnInfo(
        [property: JsonPropertyName("spawnId")] string SpawnId,
        [property: JsonPropertyName("bootstrapToken")] string BootstrapToken,
        [property: JsonPropertyName("runtimeToHostToken")] string RuntimeToHostToken,
        [property: JsonPropertyName("hostToRuntimeToken")] string HostToRuntimeToken,
        [property: JsonPropertyName("brickId")] string? BrickId,
        [property: JsonPropertyName("instanceId")] string? InstanceId);

    /// <summary>invoke/interact 的结构化结果；Ok=false 时 Error 非空。</summary>
    public sealed class DriveReply
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        /// <summary>本次调用使用的 invocationId（驱动面生成或请求方指定）。</summary>
        [JsonPropertyName("invocationId")] public string? InvocationId { get; set; }
        [JsonPropertyName("result")] public JsonElement Result { get; set; }
        [JsonPropertyName("events")] public List<JsonElement>? Events { get; set; }
        [JsonPropertyName("error")] public DriveError? Error { get; set; }
    }

    public sealed class DriveError
    {
        [JsonPropertyName("brickCode")] public string? BrickCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("grpcStatus")] public int? GrpcStatus { get; set; }
    }

    public sealed record UiCallRecord(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("args")] JsonElement Args,
        [property: JsonPropertyName("at")] long At);

    private sealed class UiCallsResponse
    {
        [JsonPropertyName("calls")] public List<UiCallRecord>? Calls { get; set; }
    }

    private sealed class CallsResponse
    {
        [JsonPropertyName("calls")] public List<RecordedCall>? Calls { get; set; }
    }

    private sealed class ReadyInfo
    {
        [JsonPropertyName("dataEndpoint")] public string? DataEndpoint { get; set; }
        [JsonPropertyName("controlEndpoint")] public string? ControlEndpoint { get; set; }
        [JsonPropertyName("runtimeToHostToken")] public string? RuntimeToHostToken { get; set; }
    }
}
