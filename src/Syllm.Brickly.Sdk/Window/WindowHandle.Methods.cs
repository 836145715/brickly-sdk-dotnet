namespace Syllm.Brickly.Sdk;

/// <summary>窗口反射方法的强类型包装，按 specs/window-api.md 分组。</summary>
public partial class WindowHandle
{
    // —— 1. 几何 / 位置（17）——

    public Task SetBoundsAsync(Bounds bounds, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setBounds", new object?[] { bounds }, cancellationToken);

    public Task<WindowRect> GetBoundsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<WindowRect>("getBounds", null, cancellationToken);

    public Task SetContentBoundsAsync(Bounds bounds, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setContentBounds", new object?[] { bounds }, cancellationToken);

    public Task<WindowRect> GetContentBoundsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<WindowRect>("getContentBounds", null, cancellationToken);

    public Task<WindowRect> GetNormalBoundsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<WindowRect>("getNormalBounds", null, cancellationToken);

    public Task SetPositionAsync(int x, int y, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setPosition", new object?[] { x, y }, cancellationToken);

    public async Task<(int X, int Y)> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        var values = await CallAsync<int[]>("getPosition", null, cancellationToken).ConfigureAwait(false);
        return (values.Length > 0 ? values[0] : 0, values.Length > 1 ? values[1] : 0);
    }

    public Task SetSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setSize", new object?[] { width, height }, cancellationToken);

    public async Task<(int Width, int Height)> GetSizeAsync(CancellationToken cancellationToken = default)
    {
        var values = await CallAsync<int[]>("getSize", null, cancellationToken).ConfigureAwait(false);
        return (values.Length > 0 ? values[0] : 0, values.Length > 1 ? values[1] : 0);
    }

    public Task SetContentSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setContentSize", new object?[] { width, height }, cancellationToken);

    public async Task<(int Width, int Height)> GetContentSizeAsync(CancellationToken cancellationToken = default)
    {
        var values = await CallAsync<int[]>("getContentSize", null, cancellationToken).ConfigureAwait(false);
        return (values.Length > 0 ? values[0] : 0, values.Length > 1 ? values[1] : 0);
    }

    public Task SetMinimumSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMinimumSize", new object?[] { width, height }, cancellationToken);

    public async Task<(int Width, int Height)> GetMinimumSizeAsync(CancellationToken cancellationToken = default)
    {
        var values = await CallAsync<int[]>("getMinimumSize", null, cancellationToken).ConfigureAwait(false);
        return (values.Length > 0 ? values[0] : 0, values.Length > 1 ? values[1] : 0);
    }

    public Task SetMaximumSizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMaximumSize", new object?[] { width, height }, cancellationToken);

    public async Task<(int Width, int Height)> GetMaximumSizeAsync(CancellationToken cancellationToken = default)
    {
        var values = await CallAsync<int[]>("getMaximumSize", null, cancellationToken).ConfigureAwait(false);
        return (values.Length > 0 ? values[0] : 0, values.Length > 1 ? values[1] : 0);
    }

    public Task SetAspectRatioAsync(double ratio, Size? extraSize = null, CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { ratio };
        if (extraSize is not null)
        {
            args.Add(extraSize);
        }
        return CallVoidAsync("setAspectRatio", args, cancellationToken);
    }

    public Task CenterAsync(CancellationToken cancellationToken = default) =>
        CallVoidAsync("center", null, cancellationToken);

    // —— 2. 状态切换（11）——

    public Task MinimizeAsync(CancellationToken cancellationToken = default) => CallVoidAsync("minimize", null, cancellationToken);

    public Task MaximizeAsync(CancellationToken cancellationToken = default) => CallVoidAsync("maximize", null, cancellationToken);

    public Task UnmaximizeAsync(CancellationToken cancellationToken = default) => CallVoidAsync("unmaximize", null, cancellationToken);

    public Task RestoreAsync(CancellationToken cancellationToken = default) => CallVoidAsync("restore", null, cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) => CallVoidAsync("hide", null, cancellationToken);

    public Task ShowAsync(CancellationToken cancellationToken = default) => CallVoidAsync("show", null, cancellationToken);

    public Task ShowInactiveAsync(CancellationToken cancellationToken = default) => CallVoidAsync("showInactive", null, cancellationToken);

    public Task FocusAsync(CancellationToken cancellationToken = default) => CallVoidAsync("focus", null, cancellationToken);

    public Task BlurAsync(CancellationToken cancellationToken = default) => CallVoidAsync("blur", null, cancellationToken);

    public Task SetFullScreenAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setFullScreen", new object?[] { flag }, cancellationToken);

    // —— 3. 状态查询（23）——

    public Task<bool> IsDestroyedAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isDestroyed", null, cancellationToken);

    public Task<bool> IsVisibleAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isVisible", null, cancellationToken);

    public Task<bool> IsFocusedAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isFocused", null, cancellationToken);

    public Task<bool> IsMinimizedAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMinimized", null, cancellationToken);

    public Task<bool> IsMaximizedAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMaximized", null, cancellationToken);

    public Task<bool> IsFullScreenAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isFullScreen", null, cancellationToken);

    public Task<bool> IsNormalAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isNormal", null, cancellationToken);

    public Task<bool> IsModalAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isModal", null, cancellationToken);

    public Task<bool> IsResizableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isResizable", null, cancellationToken);

    public Task<bool> IsMovableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMovable", null, cancellationToken);

    public Task<bool> IsFocusableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isFocusable", null, cancellationToken);

    public Task<bool> IsMinimizableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMinimizable", null, cancellationToken);

    public Task<bool> IsMaximizableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMaximizable", null, cancellationToken);

    public Task<bool> IsClosableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isClosable", null, cancellationToken);

    public Task<bool> IsFullScreenableAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isFullScreenable", null, cancellationToken);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isEnabled", null, cancellationToken);

    public Task<bool> HasShadowAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("hasShadow", null, cancellationToken);

    public Task<bool> IsAlwaysOnTopAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isAlwaysOnTop", null, cancellationToken);

    public Task<bool> IsVisibleOnAllWorkspacesAsync(CancellationToken cancellationToken = default) =>
        CallAsync<bool>("isVisibleOnAllWorkspaces", null, cancellationToken);

    public Task<bool> IsKioskAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isKiosk", null, cancellationToken);

    public Task<bool> IsMenuBarVisibleAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMenuBarVisible", null, cancellationToken);

    public Task<bool> IsMenuBarAutoHideAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isMenuBarAutoHide", null, cancellationToken);

    public Task<bool> IsDocumentEditedAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("isDocumentEdited", null, cancellationToken);

    // —— 4. 视觉属性（9）——

    public Task SetOpacityAsync(double opacity, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setOpacity", new object?[] { opacity }, cancellationToken);

    public Task<double> GetOpacityAsync(CancellationToken cancellationToken = default) =>
        CallAsync<double>("getOpacity", null, cancellationToken);

    public Task SetBackgroundColorAsync(string color, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setBackgroundColor", new object?[] { color }, cancellationToken);

    public Task SetHasShadowAsync(bool hasShadow, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setHasShadow", new object?[] { hasShadow }, cancellationToken);

    public Task SetTitleAsync(string title, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setTitle", new object?[] { title }, cancellationToken);

    public Task<string> GetTitleAsync(CancellationToken cancellationToken = default) =>
        CallAsync<string>("getTitle", null, cancellationToken);

    public Task InvalidateShadowAsync(CancellationToken cancellationToken = default) =>
        CallVoidAsync("invalidateShadow", null, cancellationToken);

    public Task FlashFrameAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("flashFrame", new object?[] { flag }, cancellationToken);

    public Task SetProgressBarAsync(
        double progress,
        ProgressBarOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { progress };
        if (options is not null)
        {
            args.Add(options);
        }
        return CallVoidAsync("setProgressBar", args, cancellationToken);
    }

    // —— 5. 层叠 / 鼠标 / 任务栏（7）——

    public Task SetAlwaysOnTopAsync(bool flag, string? level = null, CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { flag };
        if (!string.IsNullOrEmpty(level))
        {
            args.Add(level);
        }
        return CallVoidAsync("setAlwaysOnTop", args, cancellationToken);
    }

    public Task SetIgnoreMouseEventsAsync(
        bool ignore,
        IgnoreMouseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { ignore };
        if (options is not null)
        {
            args.Add(options);
        }
        return CallVoidAsync("setIgnoreMouseEvents", args, cancellationToken);
    }

    public Task SetSkipTaskbarAsync(bool skip, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setSkipTaskbar", new object?[] { skip }, cancellationToken);

    public Task SetVisibleOnAllWorkspacesAsync(
        bool visible,
        VisibleOnAllWorkspacesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { visible };
        if (options is not null)
        {
            args.Add(options);
        }
        return CallVoidAsync("setVisibleOnAllWorkspaces", args, cancellationToken);
    }

    public Task MoveTopAsync(CancellationToken cancellationToken = default) =>
        CallVoidAsync("moveTop", null, cancellationToken);

    public Task MoveAboveAsync(string mediaSourceId, CancellationToken cancellationToken = default) =>
        CallVoidAsync("moveAbove", new object?[] { mediaSourceId }, cancellationToken);

    public Task SetKioskAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setKiosk", new object?[] { flag }, cancellationToken);

    // —— 6. 能力开关（8）——

    public Task SetResizableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setResizable", new object?[] { flag }, cancellationToken);

    public Task SetMovableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMovable", new object?[] { flag }, cancellationToken);

    public Task SetFocusableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setFocusable", new object?[] { flag }, cancellationToken);

    public Task SetMinimizableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMinimizable", new object?[] { flag }, cancellationToken);

    public Task SetMaximizableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMaximizable", new object?[] { flag }, cancellationToken);

    public Task SetClosableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setClosable", new object?[] { flag }, cancellationToken);

    public Task SetFullScreenableAsync(bool flag, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setFullScreenable", new object?[] { flag }, cancellationToken);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setEnabled", new object?[] { enabled }, cancellationToken);

    // —— 7. 菜单栏（3）——

    public Task SetMenuBarVisibilityAsync(bool visible, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setMenuBarVisibility", new object?[] { visible }, cancellationToken);

    public Task SetAutoHideMenuBarAsync(bool hide, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setAutoHideMenuBar", new object?[] { hide }, cancellationToken);

    public Task RemoveMenuAsync(CancellationToken cancellationToken = default) =>
        CallVoidAsync("removeMenu", null, cancellationToken);

    // —— 8. macOS 文档窗口（3）——

    public Task SetRepresentedFilenameAsync(string path, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setRepresentedFilename", new object?[] { path }, cancellationToken);

    public Task<string> GetRepresentedFilenameAsync(CancellationToken cancellationToken = default) =>
        CallAsync<string>("getRepresentedFilename", null, cancellationToken);

    public Task SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        CallVoidAsync("setDocumentEdited", new object?[] { edited }, cancellationToken);

    // —— 9. 内容加载（3）——

    public Task LoadUrlAsync(
        string url,
        IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { url };
        if (options is not null)
        {
            args.Add(options);
        }
        return CallVoidAsync("loadURL", args, cancellationToken);
    }

    public Task LoadFileAsync(
        string path,
        IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<object?> { path };
        if (options is not null)
        {
            args.Add(options);
        }
        return CallVoidAsync("loadFile", args, cancellationToken);
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        CallVoidAsync("reload", null, cancellationToken);

    private async Task CallVoidAsync(string method, IReadOnlyList<object?>? args, CancellationToken cancellationToken)
    {
        await CallWithParentAsync(method, args, null, cancellationToken).ConfigureAwait(false);
    }
}
