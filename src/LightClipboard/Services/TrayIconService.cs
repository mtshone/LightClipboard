using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace LightClipboard.Services;

/// <summary>
/// 托盘图标与托盘菜单。程序常驻后台，托盘是入口与状态指示。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _startupItem;
    private readonly MenuItem _autoPasteItem;
    private readonly MenuItem _winVItem;
    private bool _disposed;

    public TrayIconService(AppSettings settings, string? hotkeyText)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = BuildToolTip(settings, hotkeyText),
            IconSource = LoadIconSource(),
            NoLeftClickDelay = true,
        };

        var showItem = new MenuItem { Header = "打开剪切板面板", FontWeight = FontWeights.SemiBold };
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var hotkeyItem = new MenuItem
        {
            Header = hotkeyText == null ? "全局热键未注册" : $"快捷唤醒：{hotkeyText}",
            IsEnabled = false,
        };

        _pauseItem = new MenuItem { Header = "暂停监听", IsCheckable = true, IsChecked = settings.MonitorPaused };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(this, _pauseItem.IsChecked);

        _autoPasteItem = new MenuItem { Header = "点击后自动粘贴", IsCheckable = true, IsChecked = settings.AutoPaste };
        _autoPasteItem.Click += (_, _) => AutoPasteToggled?.Invoke(this, _autoPasteItem.IsChecked);

        _startupItem = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = StartupService.IsEnabled() };
        _startupItem.Click += (_, _) => StartupToggled?.Invoke(this, _startupItem.IsChecked);

        _winVItem = new MenuItem { Header = "Win+V 拦截（本程序接管）", IsCheckable = true, IsChecked = settings.EnableWinVHook };
        _winVItem.Click += (_, _) => WinVHookToggled?.Invoke(this, _winVItem.IsChecked);

        var clearItem = new MenuItem { Header = "清空历史记录（保留收藏）" };
        clearItem.Click += (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new MenuItem { Header = "设置…" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var openDataItem = new MenuItem { Header = "打开数据目录" };
        openDataItem.Click += (_, _) => OpenDataFolderRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new MenuItem { Header = "退出 LightClipboard" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(hotkeyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autoPasteItem);
        menu.Items.Add(_winVItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(clearItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(openDataItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon.ContextMenu = menu;
        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? ClearRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? OpenDataFolderRequested;

    public event EventHandler<bool>? PauseToggled;

    public event EventHandler<bool>? AutoPasteToggled;

    public event EventHandler<bool>? StartupToggled;

    public event EventHandler<bool>? WinVHookToggled;

    /// <summary>同步菜单勾选状态。</summary>
    public void SyncState(AppSettings settings)
    {
        _pauseItem.IsChecked = settings.MonitorPaused;
        _autoPasteItem.IsChecked = settings.AutoPaste;
        _startupItem.IsChecked = StartupService.IsEnabled();
        _winVItem.IsChecked = settings.EnableWinVHook;
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            Log.Warn("显示托盘气泡失败", ex);
        }
    }

    private static string BuildToolTip(AppSettings settings, string? hotkeyText)
    {
        string hotkey = hotkeyText == null ? "（热键未注册）" : hotkeyText;
        return $"LightClipboard\n点击打开面板  {hotkey}";
    }

    private static BitmapSource LoadIconSource()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.UriSource = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("加载托盘图标失败", ex);
            // 兜底：用一个纯色小方块，保证托盘图标仍然出现
            var fallback = new WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            fallback.Freeze();
            return fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放托盘图标失败", ex);
        }
    }
}
