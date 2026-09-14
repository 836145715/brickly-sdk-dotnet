namespace Syllm.Brickly.Sdk;

/// <summary>Brick 级 UI 门面，提供子窗口相关能力（binding=session）。</summary>
public sealed class UI
{
    private readonly BricklyRuntime _runtime;

    internal UI(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>创建子窗口并返回句柄；url 可为 http(s)、file:// 或 ui/ 相对 html。</summary>
    public Task<WindowHandle> CreateBrowserWindowAsync(
        string url,
        WindowOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _runtime.CreateBrowserWindowAsync(url, WindowBinding.NormalizeSession(options), null, cancellationToken);

    /// <summary>列出本 Brick 持有的窗口描述（结构由宿主定义）。</summary>
    public Task<List<Dictionary<string, object?>>> ListWindowsAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallListAsync("ui.window.list", null, cancellationToken);
}

/// <summary>command 作用域 UI 门面（binding=call）；webContents.Send 携带当前 command parent。</summary>
public sealed class ScopedUI
{
    private readonly BricklyRuntime _runtime;
    private readonly string _parentRequestId;

    internal ScopedUI(BricklyRuntime runtime, string parentRequestId, TraceContext? trace)
    {
        _runtime = runtime;
        _parentRequestId = parentRequestId;
        _ = trace;
    }

    /// <summary>创建 Call 窗口；随这次调用消失。</summary>
    public Task<WindowHandle> CreateBrowserWindowAsync(
        string url,
        WindowOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _runtime.CreateBrowserWindowAsync(
            url,
            WindowBinding.NormalizeCall(options),
            _parentRequestId,
            cancellationToken);

    public Task<List<Dictionary<string, object?>>> ListWindowsAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallListAsync("ui.window.list", null, cancellationToken);
}
