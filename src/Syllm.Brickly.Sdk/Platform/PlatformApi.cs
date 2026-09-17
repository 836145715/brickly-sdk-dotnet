using System.Text.Json.Serialization;

namespace Syllm.Brickly.Sdk;

/// <summary>物理屏幕或 DIP 坐标点。</summary>
public sealed class ScreenPoint
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }
}

/// <summary>物理屏幕或 DIP 坐标矩形。</summary>
public sealed class ScreenRect
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }
}

/// <summary>input.keyboardTap 的输入。</summary>
public sealed class KeyboardTapPayload
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("modifiers")]
    public IReadOnlyList<string>? Modifiers { get; init; }
}

/// <summary>汇总宿主平台能力，与 Node SDK 的 brick.platform / ctx.platform 对齐。</summary>
public sealed class PlatformApi
{
    internal PlatformApi(BricklyRuntime runtime)
    {
        Clipboard = new ClipboardApi(runtime);
        Screenshot = new ScreenshotApi(runtime);
        Screen = new ScreenApi(runtime);
        Input = new InputApi(runtime);
        System = new SystemApi(runtime);
        Search = new SearchApi(runtime);
    }

    public ClipboardApi Clipboard { get; }

    public ScreenshotApi Screenshot { get; }

    public ScreenApi Screen { get; }

    public InputApi Input { get; }

    public SystemApi System { get; }

    /// <summary>快速搜索消费通道；manifest 须声明 quickSearch.consumer。</summary>
    public SearchApi Search { get; }
}

/// <summary>runtime 剪贴板读写能力，对应 PlatformService clipboard.*。</summary>
public sealed class ClipboardApi
{
    private readonly BricklyRuntime _runtime;

    internal ClipboardApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>读取当前系统剪贴板快照。</summary>
    public Task<Dictionary<string, object?>> ReadContentAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("clipboard.readContent", null, cancellationToken);

    /// <summary>写入系统剪贴板内容。</summary>
    public Task<Dictionary<string, object?>> SetContentAsync(
        IReadOnlyDictionary<string, object?> content,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("clipboard.setContent", content, cancellationToken);
}

/// <summary>交互式区域截图能力。</summary>
public sealed class ScreenshotApi
{
    private readonly BricklyRuntime _runtime;

    internal ScreenshotApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>让用户选择屏幕区域并返回截图结果。</summary>
    public Task<Dictionary<string, object?>> SelectRegionAsync(
        IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screenshot.selectRegion", options, cancellationToken);
}

/// <summary>屏幕截图、显示器查询和坐标转换能力。</summary>
public sealed class ScreenApi
{
    private readonly BricklyRuntime _runtime;

    internal ScreenApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<Dictionary<string, object?>> CaptureRegionAsync(
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screen.captureRegion", options, cancellationToken);

    public Task<Dictionary<string, object?>> PickColorAsync(
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screen.pickColor", options, cancellationToken);

    public Task<Dictionary<string, object?>> GetPrimaryDisplayAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screen.getPrimaryDisplay", null, cancellationToken);

    public Task<List<Dictionary<string, object?>>> GetAllDisplaysAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallListAsync("screen.getAllDisplays", null, cancellationToken);

    public Task<ScreenPoint> GetCursorScreenPointAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallAsync<ScreenPoint>("screen.getCursorScreenPoint", null, cancellationToken);

    public Task<Dictionary<string, object?>> GetDisplayNearestPointAsync(
        ScreenPoint point,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screen.getDisplayNearestPoint", point, cancellationToken);

    public Task<Dictionary<string, object?>> GetDisplayMatchingAsync(
        ScreenRect rect,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallMapAsync("screen.getDisplayMatching", rect, cancellationToken);

    public Task<ScreenPoint> ScreenToDipPointAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallAsync<ScreenPoint>("screen.screenToDipPoint", point, cancellationToken);

    public Task<ScreenPoint> DipToScreenPointAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallAsync<ScreenPoint>("screen.dipToScreenPoint", point, cancellationToken);

    public Task<ScreenRect> ScreenToDipRectAsync(ScreenRect rect, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallAsync<ScreenRect>("screen.screenToDipRect", rect, cancellationToken);

    public Task<ScreenRect> DipToScreenRectAsync(ScreenRect rect, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallAsync<ScreenRect>("screen.dipToScreenRect", rect, cancellationToken);

    public Task<List<Dictionary<string, object?>>> DesktopCaptureSourcesAsync(
        IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallListAsync("screen.desktopCaptureSources", options, cancellationToken);
}

/// <summary>键盘与鼠标自动化能力。</summary>
public sealed class InputApi
{
    private readonly BricklyRuntime _runtime;

    internal InputApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task KeyboardTapAsync(KeyboardTapPayload payload, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("input.keyboardTap", payload, cancellationToken);

    public Task MouseMoveAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("input.mouseMove", point, cancellationToken);

    public Task MouseClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("input.mouseClick", point, cancellationToken);

    public Task MouseDoubleClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("input.mouseDoubleClick", point, cancellationToken);

    public Task MouseRightClickAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("input.mouseRightClick", point, cancellationToken);
}
