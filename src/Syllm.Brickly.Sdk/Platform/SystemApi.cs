namespace Syllm.Brickly.Sdk;

/// <summary>system.getPath 支持的 Electron 路径名。</summary>
public enum SystemPathName
{
    Home,
    AppData,
    Assets,
    UserData,
    SessionData,
    Temp,
    Exe,
    Module,
    Desktop,
    Documents,
    Downloads,
    Music,
    Pictures,
    Videos,
    Recent,
    Logs,
    CrashDumps,
}

/// <summary>提供宿主系统能力，对应 PlatformService system.*。</summary>
public sealed class SystemApi
{
    private readonly BricklyRuntime _runtime;

    internal SystemApi(BricklyRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>发送系统通知；clickFeatureCode 为空时不写入请求字段。</summary>
    public Task ShowNotificationAsync(
        string body,
        string? clickFeatureCode = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?> { ["body"] = body };
        if (!string.IsNullOrEmpty(clickFeatureCode))
        {
            payload["clickFeatureCode"] = clickFeatureCode;
        }
        return _runtime.PlatformCallVoidAsync("system.showNotification", payload, cancellationToken);
    }

    /// <summary>使用系统默认方式打开本地路径。</summary>
    public Task ShellOpenPathAsync(string fullPath, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("system.shellOpenPath", fullPath, cancellationToken);

    /// <summary>将本地路径移入系统回收站。</summary>
    public Task ShellTrashItemAsync(string fullPath, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("system.shellTrashItem", fullPath, cancellationToken);

    /// <summary>在系统文件管理器中定位指定路径。</summary>
    public Task ShellShowItemInFolderAsync(string fullPath, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("system.shellShowItemInFolder", fullPath, cancellationToken);

    /// <summary>使用系统默认方式打开外部 URL；仅允许 http / https / mailto。</summary>
    public Task ShellOpenExternalAsync(string url, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("system.shellOpenExternal", url, cancellationToken);

    /// <summary>播放系统提示音。</summary>
    public Task ShellBeepAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallVoidAsync("system.shellBeep", null, cancellationToken);

    /// <summary>返回宿主提供的设备标识。</summary>
    public Task<string> GetNativeIdAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.getNativeId", null, cancellationToken);

    /// <summary>返回当前应用名称。</summary>
    public Task<string> GetAppNameAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.getAppName", null, cancellationToken);

    /// <summary>返回当前应用版本。</summary>
    public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.getAppVersion", null, cancellationToken);

    /// <summary>返回指定 Electron 路径。</summary>
    public Task<string> GetPathAsync(SystemPathName name, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.getPath", ToWire(name), cancellationToken);

    /// <summary>返回指定文件、扩展名或 folder 的图标 Data URL。</summary>
    public Task<string> GetFileIconAsync(string filePath, CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.getFileIcon", filePath, cancellationToken);

    /// <summary>读取当前前台文件管理器路径。</summary>
    public Task<string> ReadCurrentFolderPathAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.readCurrentFolderPath", null, cancellationToken);

    /// <summary>预留接口；当前宿主返回 UNSUPPORTED_PLATFORM。</summary>
    public Task<string> ReadCurrentBrowserUrlAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallStringAsync("system.readCurrentBrowserUrl", null, cancellationToken);

    public Task<bool> IsDevAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallBoolAsync("system.isDev", null, cancellationToken);

    public Task<bool> IsMacOsAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallBoolAsync("system.isMacOS", null, cancellationToken);

    public Task<bool> IsWindowsAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallBoolAsync("system.isWindows", null, cancellationToken);

    public Task<bool> IsLinuxAsync(CancellationToken cancellationToken = default) =>
        _runtime.PlatformCallBoolAsync("system.isLinux", null, cancellationToken);

    private static string ToWire(SystemPathName name) => name switch
    {
        SystemPathName.Home => "home",
        SystemPathName.AppData => "appData",
        SystemPathName.Assets => "assets",
        SystemPathName.UserData => "userData",
        SystemPathName.SessionData => "sessionData",
        SystemPathName.Temp => "temp",
        SystemPathName.Exe => "exe",
        SystemPathName.Module => "module",
        SystemPathName.Desktop => "desktop",
        SystemPathName.Documents => "documents",
        SystemPathName.Downloads => "downloads",
        SystemPathName.Music => "music",
        SystemPathName.Pictures => "pictures",
        SystemPathName.Videos => "videos",
        SystemPathName.Recent => "recent",
        SystemPathName.Logs => "logs",
        SystemPathName.CrashDumps => "crashDumps",
        _ => "home",
    };
}
