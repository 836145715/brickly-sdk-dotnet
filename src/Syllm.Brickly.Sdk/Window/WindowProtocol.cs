namespace Syllm.Brickly.Sdk;

/// <summary>窗口反射方法白名单，与 specs/window-protocol.schema.json 的 BrickWindowMethod.enum 对齐。</summary>
public static class BrickWindowMethods
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "setBounds",
        "getBounds",
        "setPosition",
        "getPosition",
        "setSize",
        "getSize",
        "setOpacity",
        "getOpacity",
        "setAlwaysOnTop",
        "isAlwaysOnTop",
        "setIgnoreMouseEvents",
        "setSkipTaskbar",
        "setTitle",
        "getTitle",
        "setResizable",
        "setMovable",
        "setFocusable",
        "setHasShadow",
        "setBackgroundColor",
        "setVisibleOnAllWorkspaces",
        "minimize",
        "maximize",
        "unmaximize",
        "restore",
        "hide",
        "show",
        "showInactive",
        "focus",
        "blur",
        "isDestroyed",
        "isVisible",
        "isFocused",
        "isMinimized",
        "isMaximized",
        "loadURL",
        "loadFile",
        "reload",
        "webContents.send",
        "webContents.executeJavaScript",
        "webContents.openDevTools",
        "webContents.closeDevTools",
        "setContentBounds",
        "getContentBounds",
        "setContentSize",
        "getContentSize",
        "setMinimumSize",
        "getMinimumSize",
        "setMaximumSize",
        "getMaximumSize",
        "setAspectRatio",
        "center",
        "moveTop",
        "moveAbove",
        "setFullScreen",
        "isFullScreen",
        "isNormal",
        "isModal",
        "getNormalBounds",
        "isResizable",
        "isMovable",
        "isFocusable",
        "setMinimizable",
        "isMinimizable",
        "setMaximizable",
        "isMaximizable",
        "setClosable",
        "isClosable",
        "setFullScreenable",
        "isFullScreenable",
        "setEnabled",
        "isEnabled",
        "hasShadow",
        "isVisibleOnAllWorkspaces",
        "setKiosk",
        "isKiosk",
        "flashFrame",
        "setProgressBar",
        "setMenuBarVisibility",
        "isMenuBarVisible",
        "setAutoHideMenuBar",
        "isMenuBarAutoHide",
        "removeMenu",
        "invalidateShadow",
        "setRepresentedFilename",
        "getRepresentedFilename",
        "setDocumentEdited",
        "isDocumentEdited",
        "webContents.toggleDevTools",
        "webContents.isDevToolsOpened",
        "webContents.goBack",
        "webContents.goForward",
        "webContents.canGoBack",
        "webContents.canGoForward",
        "webContents.getURL",
        "webContents.getTitle",
        "webContents.setZoomFactor",
        "webContents.getZoomFactor",
        "webContents.setZoomLevel",
        "webContents.getZoomLevel",
        "webContents.copy",
        "webContents.paste",
        "webContents.cut",
        "webContents.selectAll",
        "webContents.undo",
        "webContents.redo",
        "startDrag",
        "endDrag",
    };
}

/// <summary>关闭状态。</summary>
public static class WindowCloseStatuses
{
    public const string Closed = "closed";
    public const string Prevented = "prevented";
    public const string Pending = "pending";
    public const string NotFound = "not-found";
}

/// <summary>终态事件状态。</summary>
public static class WindowTerminalEventStatuses
{
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>原生窗状态。</summary>
public static class WindowNativeStatuses
{
    public const string Destroyed = "destroyed";
    public const string AlreadyDestroyed = "already-destroyed";
    public const string Failed = "failed";
}

/// <summary>普通关闭请求结果。</summary>
public sealed class WindowRequestCloseResult
{
    public string Status { get; init; } = string.Empty;
}

/// <summary>强制终止错误明细。</summary>
public sealed class WindowTerminationError
{
    public string Step { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>强制终止结果。</summary>
public sealed class WindowTerminationResult
{
    public string Event { get; init; } = string.Empty;
    public string Window { get; init; } = string.Empty;
    public List<WindowTerminationError> Errors { get; init; } = new();
}

/// <summary>窗口几何入参；全部字段可选。</summary>
public sealed class Bounds
{
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

/// <summary>窗口矩形返回值。</summary>
public sealed class WindowRect
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>SetAspectRatio 的 extraSize 参数。</summary>
public sealed class Size
{
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>SetProgressBar 选项。</summary>
public sealed class ProgressBarOptions
{
    public string? Mode { get; init; }
}

/// <summary>SetIgnoreMouseEvents 选项。</summary>
public sealed class IgnoreMouseOptions
{
    public bool Forward { get; init; }
}

/// <summary>SetVisibleOnAllWorkspaces 选项。</summary>
public sealed class VisibleOnAllWorkspacesOptions
{
    public bool VisibleOnFullScreen { get; init; }
}

/// <summary>WebContents.OpenDevTools 选项。</summary>
public sealed class OpenDevToolsOptions
{
    public string? Mode { get; init; }
}

/// <summary>窗口创建 options；用字典保持与 schema 自由演进同步。</summary>
public sealed class WindowOptions : Dictionary<string, object?>
{
    public WindowOptions()
    {
    }

    public WindowOptions(IDictionary<string, object?> source)
        : base(source)
    {
    }
}
