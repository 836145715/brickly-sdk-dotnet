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
