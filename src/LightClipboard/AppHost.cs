using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LightClipboard.Services;
using LightClipboard.ViewModels;
using LightClipboard.Views;

namespace LightClipboard;

/// <summary>
/// 组合根：按依赖顺序创建并持有全部服务（轻量级 DI，不引入容器）。
/// </summary>
public sealed class AppHost : IDisposable
{
    private bool _disposed;

    public AppHost()
    {
        Current = this;

        AppPaths.EnsureCreated();

        SettingsService = new SettingsService();
        SettingsService.Load();

        Storage = new StorageService(AppPaths.DatabasePath);
        Images = new ImageCacheManager(SettingsService.Current.ThumbnailWidth);
        Writer = new ClipboardWriter(Images);
        Parser = new ClipboardDataParser(SettingsService.Current);
        Monitor = new ClipboardMonitorService();
        Hotkeys = new GlobalHotkeyService();
        DragDrop = new DragDropService(Images);
        KeyboardHook = new KeyboardHookService();

        Main = new MainViewModel(Storage, Images, Writer, SettingsService, KeyboardHook);
        Main.Load();

        Monitor.IsPaused = SettingsService.Current.MonitorPaused;

        // 设置页 / 托盘菜单改动“Win+V 拦截”后，统一在这里启动或停止钩子
        SettingsService.Changed += OnSettingsChanged;
    }

    public static AppHost? Current { get; private set; }

    public SettingsService SettingsService { get; }

    public StorageService Storage { get; }

    public ImageCacheManager Images { get; }

    public ClipboardWriter Writer { get; }

    public ClipboardDataParser Parser { get; }

    public ClipboardMonitorService Monitor { get; }

    public GlobalHotkeyService Hotkeys { get; }

    public DragDropService DragDrop { get; }

    public KeyboardHookService KeyboardHook { get; }

    public MainViewModel Main { get; }

    public TrayIconService? Tray { get; private set; }

    public MainWindow? Window { get; private set; }

    /// <summary>窗口句柄就绪后绑定依赖 HWND 的服务（剪切板监听、全局热键、托盘）。</summary>
    public void AttachWindow(MainWindow window)
    {
        Window = window;
        IntPtr handle = window.WindowHandle;

        Monitor.IsPaused = SettingsService.Current.MonitorPaused;
        Monitor.ClipboardUpdated += OnClipboardUpdated;
        Monitor.Start(handle);

        if (SettingsService.Current.HotkeyEnabled)
        {
            string? hotkey = Hotkeys.Register(handle);
            Main.HotkeyText = hotkey ?? "未注册";
        }
        else
        {
            Main.HotkeyText = "已禁用";
        }

        Hotkeys.Pressed += (_, _) => window.TogglePanel();

        // Win+V 拦截：钩子只负责“截获”，唤起的动作与热键保持一致（面板已显示且聚焦时再按 = 收起）
        KeyboardHook.WinVPressed += (_, _) => window.TogglePanel();
        ApplyWinVHook();

        Tray = new TrayIconService(SettingsService.Current, Main.HotkeyText);
        Tray.ShowRequested += (_, _) => window.TogglePanel();
        Tray.ExitRequested += (_, _) => Application.Current.Shutdown();
        Tray.ClearRequested += (_, _) => Main.ClearAllCommand.Execute(null);
        Tray.OpenDataFolderRequested += (_, _) => Main.OpenDataFolderCommand.Execute(null);
        Tray.SettingsRequested += (_, _) =>
        {
            Main.SetPageCommand.Execute("settings");
            window.ShowPanel();
        };
        Tray.PauseToggled += (_, paused) =>
        {
            Main.MonitorPaused = paused;
            Monitor.IsPaused = paused;
        };
        Tray.AutoPasteToggled += (_, enabled) => Main.AutoPaste = enabled;
        Tray.StartupToggled += (_, enabled) => Main.RunAtStartup = enabled;
        Tray.WinVHookToggled += (_, enabled) => Main.WinVHookEnabled = enabled;
    }

    /// <summary>按设置挂载/卸载 Win+V 低级键盘钩子（窗口就绪后才真正挂载）。</summary>
    private void ApplyWinVHook()
    {
        if (_disposed || Window == null)
        {
            // 窗口未就绪时先不动：AttachWindow 里会再调用一次
            return;
        }

        if (SettingsService.Current.EnableWinVHook)
        {
            KeyboardHook.Start();
        }
        else
        {
            KeyboardHook.Stop();
        }

        Main.RefreshWinVHookState();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyWinVHook();

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        // 自身写入剪切板时跳过，避免“复制 → 监听 → 再复制”的死循环
        if (Writer.IsSuppressed || Monitor.IsPaused)
        {
            return;
        }

        if (!Parser.TryRead(out var content) || content == null)
        {
            return;
        }

        // 图片落盘等重活放到异步流程里，UI 线程只做读取
        _ = Main.IngestAsync(content).ContinueWith(
            t => Log.Error("入库剪切板内容时发生异常", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>启动时的维护动作：清理孤儿图片、裁剪超限历史。</summary>
    public void RunStartupMaintenance()
    {
        try
        {
            var removed = Storage.Prune(SettingsService.Current.MaxItems);
            foreach (var item in removed)
            {
                if (item.Type == Models.ClipboardItemType.Image)
                {
                    Images.DeleteImageFile(item.ImagePath);
                }
            }

            var referenced = Storage.GetAll()
                .Where(i => !string.IsNullOrEmpty(i.ImagePath))
                .Select(i => i.ImagePath);
            Images.CollectGarbage(referenced);

            if (removed.Count > 0)
            {
                Main.Load();
            }

            Log.Info($"启动维护完成：当前 {Storage.Count()} 条记录");
        }
        catch (Exception ex)
        {
            Log.Warn("启动维护失败", ex);
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开日志目录失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Current = null;

        Monitor.ClipboardUpdated -= OnClipboardUpdated;
        Monitor.Dispose();
        Hotkeys.Dispose();
        SettingsService.Changed -= OnSettingsChanged;
        KeyboardHook.Dispose();
        Tray?.Dispose();
        Images.ClearThumbnailCache();
        Storage.Dispose();
        SettingsService.Save();
        Log.Info("LightClipboard 已退出");
    }
}
