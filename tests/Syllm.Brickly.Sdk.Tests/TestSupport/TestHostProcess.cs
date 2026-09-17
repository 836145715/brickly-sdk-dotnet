using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Syllm.Brickly.Sdk.Tests.TestSupport;

/// <summary>
/// 真宿主测试工件进程包装：spawn @syllm/brickly-test-host 的 dist/host.cjs，
/// 解析 stdout 就绪 JSON，提供控制面 HTTP 客户端。
/// bundle 查找：BRICKLY_TEST_HOST_BUNDLE 环境变量优先，否则从输出目录向上
/// 找 node_modules/@syllm/brickly-test-host/dist/host.cjs（monorepo 命中根
/// workspace 链接；独立仓库命中本仓 node_modules）。
/// </summary>
public sealed class TestHostProcess : IDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http = new();

    public string DataEndpoint { get; private set; } = string.Empty;
    public string ControlEndpoint { get; private set; } = string.Empty;
    public string RuntimeToHostToken { get; private set; } = string.Empty;

    /// <summary>最近一次 SpawnAsync 铸出的凭据（StartRuntimeAsync 默认 spawn 的那套）。</summary>
    public SpawnInfo? LastSpawn { get; private set; }

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

    // 每层先探 monorepo 本地构建产物（packages/brickly-test-host/dist，
    // 反映当前源码），再探 node_modules 发布版（独立仓库唯一来源）。
    // worktree 下 node_modules 里的包可能 junction 到主工作区旧构建，故本地产物优先。
    private static string? FindBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var local = Path.Combine(
                dir.FullName, "packages", "brickly-test-host", "dist", "host.cjs");
            if (File.Exists(local))
            {
                return local;
            }
            var published = Path.Combine(
                dir.FullName, "node_modules", "@syllm", "brickly-test-host", "dist", "host.cjs");
            if (File.Exists(published))
            {
                return published;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>故障注入：error 短路（brickCode）/hang（挂死）/delay（延迟）。</summary>
    public async Task SetFaultAsync(string path, string brickCode, int count = 1)
    {
        var response = await _http.PostAsJsonAsync(
            "/faults", new { path, brickCode, count }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>hang 剧本：命中 path 的调用永不应答（客户端 deadline/取消路径测试）。</summary>
    public async Task SetHangFaultAsync(string path)
    {
        var response = await _http.PostAsJsonAsync(
            "/faults", new { path, action = "hang" }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>写入 Runtime 启动环境变量（spawn 凭据 → BRICKLY_* 进程环境）。</summary>
    public void ApplyEnvironment(
        SpawnInfo? spawn = null,
        string? dependencyBindings = null,
        string? profileConfig = null)
    {
        var creds = spawn ?? LastSpawn
            ?? throw new InvalidOperationException("尚无 spawn 凭据，先 SpawnAsync");
        Environment.SetEnvironmentVariable("BRICKLY_HOST_ENDPOINT", DataEndpoint);
        Environment.SetEnvironmentVariable("BRICKLY_BOOTSTRAP_TOKEN", creds.BootstrapToken);
        Environment.SetEnvironmentVariable("BRICKLY_RUNTIME_TO_HOST_TOKEN", creds.RuntimeToHostToken);
        Environment.SetEnvironmentVariable("BRICKLY_HOST_TO_RUNTIME_TOKEN", creds.HostToRuntimeToken);
        Environment.SetEnvironmentVariable("BRICKLY_DEPENDENCY_BINDINGS", dependencyBindings);
        Environment.SetEnvironmentVariable("BRICKLY_PROFILE_CONFIG", profileConfig);
    }

    /// <summary>清理全部 BRICKLY_* 环境变量（替代 FakeHost.ClearEnvironment）。</summary>
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

    /// <summary>注册 search.* 平台方法罐头响应（method ∈ query/activate/runAction）。</summary>
    public async Task SetSearchResponseAsync(string method, object? result)
    {
        var response = await _http.PostAsJsonAsync(
            "/search-handler", new { method, result }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>search.* 被调记录（method/input/callerBrickId），/reset 清空。</summary>
    public async Task<IReadOnlyList<SearchCallRecord>> SearchCallsAsync()
    {
        var body = await _http.GetFromJsonAsync<SearchCallsResponse>("/search-calls").ConfigureAwait(false);
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
        LastSpawn = info;
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

    /// <summary>
    /// 任意 wire 平台方法罐头（clipboard.* / screen.* / input.* / system.* /
    /// runtime.invoke；'$interact' 为 PlatformService.Interact 最终结果罐头）。
    /// 重复注册覆盖。
    /// </summary>
    public async Task SetPlatformResponseAsync(string method, object? result)
    {
        _ = await PostAsync<JsonElement>("/platform-handler", new { method, result }).ConfigureAwait(false);
    }

    /// <summary>
    /// interact 裸帧剧本：语义违规帧注入与收流断言（替代 FakeInteractSession）。
    /// spawnId 缺省时注入 LastSpawn——HTTP 驱动面的 waitHandle 默认等的是宿主自带
    /// 初始 spawn（无 runtime 注册），不显式指定会等超时。
    /// </summary>
    public async Task<InteractScriptReply> InteractScriptAsync(object body)
    {
        var payload = JsonSerializer.SerializeToNode(body)!.AsObject();
        if (payload["spawnId"] is null && LastSpawn is not null)
        {
            payload["spawnId"] = LastSpawn.SpawnId;
        }
        return await PostAsync<InteractScriptReply>("/interact-script", payload).ConfigureAwait(false);
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

    /// <summary>轮询 /calls 直到出现谓词命中的录制（事件订阅建立等异步等待用）。</summary>
    public async Task<RecordedCall> WaitCallAsync(
        Func<RecordedCall, bool> predicate, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var calls = await CallsAsync().ConfigureAwait(false);
            var found = calls.FirstOrDefault(predicate);
            if (found is not null)
            {
                return found;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"等待录制调用超时；当前 /calls=[{string.Join(", ", calls.Select(c => c.Path))}]");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 第 registerIndex 个 runtime 注册时上报的 endpoint（多 runtime 场景按注册顺序取，
    /// 后启动的 runtime 用更大的下标）。
    /// </summary>
    public async Task<string> RuntimeEndpointAsync(int timeoutMs = 8000, int registerIndex = 0)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var calls = await CallsAsync().ConfigureAwait(false);
            var registers = calls
                .Where(item => item.Path.EndsWith("RuntimeRegistry/Register", StringComparison.Ordinal))
                .ToList();
            if (registers.Count > registerIndex)
            {
                var endpoint = registers[registerIndex].Request.TryGetProperty("endpoint", out var value)
                    ? value.GetString()
                    : null;
                return endpoint ?? throw new InvalidOperationException("Register 录制缺 endpoint 字段");
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"等待第 {registerIndex + 1} 次 Register 录制超时；当前 /calls=[{string.Join(", ", calls.Select(c => c.Path))}]");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public sealed record RecordedCall(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("at")] long At,
        [property: JsonPropertyName("invocationId")] string? InvocationId,
        [property: JsonPropertyName("intent")] string? Intent,
        [property: JsonPropertyName("request")] JsonElement Request,
        [property: JsonPropertyName("frames")] int Frames);

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

    /// <summary>/interact-script 结构化结果。</summary>
    public sealed class InteractScriptReply
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("received")] public List<InteractInbound>? Received { get; set; }
        [JsonPropertyName("failedStep")] public int? FailedStep { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    /// <summary>剧本面入站帧录制（kind: opened/event/response/final/end/error）。</summary>
    public sealed class InteractInbound
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("sequence")] public long Sequence { get; set; }
        [JsonPropertyName("messageId")] public string? MessageId { get; set; }
        [JsonPropertyName("replyTo")] public string? ReplyTo { get; set; }
        [JsonPropertyName("event")] public JsonElement Event { get; set; }
        [JsonPropertyName("response")] public JsonElement Response { get; set; }
        [JsonPropertyName("final")] public JsonElement Final { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
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

    public sealed record SearchCallRecord(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("input")] JsonElement Input,
        [property: JsonPropertyName("callerBrickId")] string CallerBrickId,
        [property: JsonPropertyName("at")] long At);

    private sealed class SearchCallsResponse
    {
        [JsonPropertyName("calls")] public List<SearchCallRecord>? Calls { get; set; }
    }

    private sealed class ReadyInfo
    {
        [JsonPropertyName("dataEndpoint")] public string? DataEndpoint { get; set; }
        [JsonPropertyName("controlEndpoint")] public string? ControlEndpoint { get; set; }
        [JsonPropertyName("runtimeToHostToken")] public string? RuntimeToHostToken { get; set; }
    }
}
