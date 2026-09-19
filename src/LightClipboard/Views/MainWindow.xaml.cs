using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightClipboard.Data;
using LightClipboard.Interop;
using LightClipboard.Services;
using LightClipboard.ViewModels;
using Wpf.Ui.Controls;

namespace LightClipboard.Views;

/// <summary>
/// 主面板窗口：一个常驻后台、按需弹出的 Fluent 卡片面板。
/// 视图层的职责：窗口定位、焦点与隐藏策略、拖拽交互、键盘快捷键。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _deferredHideTimer;

    private IntPtr _previousForeground = IntPtr.Zero;
    private bool _modalOpen;
    private bool _isDragging;
    private bool _dragMoved;
    private bool _allowClose;
    private bool _showInProgress;
    private bool _chipClickInProgress;
    private Point _dragStart;

    public MainWindow(AppHost host)
    {
        _host = host;
        _vm = host.Main;

        InitializeComponent();

        DataContext = _vm;

        // “失焦即隐藏”需要在拖拽场景下让路：用户从资源管理器按住文件拖过来时，
        // 本窗口会先失焦，如果立刻隐藏就没法接收拖放了。这里延迟一小段时间再隐藏，
        // 只要窗口收到 DragEnter/DragOver，就取消这次隐藏。
        // 延迟时长由设置项 DeferredHideDelayMs 决定（设置页可调），默认 700ms。
        _deferredHideTimer = new DispatcherTimer { Interval = CurrentDeferredHideDelay() };
        _deferredHideTimer.Tick += OnDeferredHideTick;

        // 设置页改动“隐藏延迟”后立即生效（不必重开面板）
        _host.SettingsService.Changed += OnSettingsChanged;

        Deactivated += OnWindowDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        DragEnter += OnWindowDragEnter;
        DragOver += OnWindowDragOver;
        DragLeave += OnWindowDragLeave;
        Drop += OnWindowDrop;

        _vm.PasteRequested += OnPasteRequested;
        _vm.ModalStateChanged += OnModalStateChanged;

        RestoreWindowSize();
    }

    /// <summary>当前设置下的隐藏缓冲时长（已在 SettingsService 里钳制过）。</summary>
    private TimeSpan CurrentDeferredHideDelay()
        => TimeSpan.FromMilliseconds(_host.SettingsService.Current.DeferredHideDelayMs);

    /// <summary>设置变化：把新的延迟时长套用到计时器（拖拽缓冲的“700ms 感知过强”问题由此解决）。</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var delay = CurrentDeferredHideDelay();
        if (_deferredHideTimer.Interval != delay)
        {
            _deferredHideTimer.Interval = delay;
            Log.Info($"隐藏缓冲时长已更新为 {delay.TotalMilliseconds:0} ms");
        }
    }

    /// <summary>恢复上次使用的面板尺寸。</summary>
    private void RestoreWindowSize()
    {
        var settings = _host.SettingsService.Current;
        if (settings.WindowWidth >= MinWidth)
        {
            Width = settings.WindowWidth;
        }

        if (settings.WindowHeight >= MinHeight)
        {
            Height = settings.WindowHeight;
        }
    }

    /// <summary>记住当前面板尺寸（在收起面板时调用，避免拖动过程中频繁写盘）。</summary>
    private void PersistWindowSize()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        if (width < MinWidth || height < MinHeight || double.IsNaN(width) || double.IsNaN(height))
        {
            return;
        }

        var settings = _host.SettingsService.Current;
        int w = (int)Math.Round(width);
        int h = (int)Math.Round(height);
        if (settings.WindowWidth == w && settings.WindowHeight == h)
        {
            return;
        }

        _host.SettingsService.Update(s =>
        {
            s.WindowWidth = w;
            s.WindowHeight = h;
        });
    }

    /// <summary>窗口句柄（OnSourceInitialized 之后有效）。</summary>
    public IntPtr WindowHandle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHandle = new WindowInteropHelper(this).Handle;
        _host.AttachWindow(this);
        Log.Info($"主窗口已创建 (HWND 0x{WindowHandle.ToInt64():X})");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // 关闭按钮 = 收起到托盘，程序继续在后台监听
            e.Cancel = true;
            HidePanel();
            return;
        }

        PersistWindowSize();
        _host.SettingsService.Changed -= OnSettingsChanged;
        base.OnClosing(e);
    }

    /// <summary>真正退出时调用，允许窗口关闭。</summary>
    public void AllowClose() => _allowClose = true;

    // ==================================================================
    // 显示 / 隐藏
    // ==================================================================

    /// <summary>切换面板显示状态（托盘图标、全局热键、二次启动共用）。</summary>
    public void TogglePanel()
    {
        if (IsVisible && IsActive)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    /// <summary>显示面板并把焦点交给搜索框。</summary>
    public void ShowPanel()
    {
        _deferredHideTimer.Stop();

        // 记录“上一次的前台窗口”，点击卡片后把焦点还给它
        IntPtr foreground = PasteService.GetExternalForegroundWindow(WindowHandle);
        if (foreground != IntPtr.Zero)
        {
            _previousForeground = foreground;
        }

        _vm.OnPanelShown();

        // 显示过程中系统可能先给一个 WM_ACTIVATE(INACTIVE)（本进程抢前台需要一点时间），
        // 期间必须忽略“失焦即隐藏”，否则面板刚弹出就被自己收起来。
        // 解除动作放在 FocusSearchBox 的延时回调里（等激活消息处理完）。
        _showInProgress = true;

        if (!IsVisible)
        {
            // 先以透明状态显示并完成定位，避免出现窗口从默认位置跳到目标位置
            Opacity = 0;
            Show();
            PositionOnCurrentScreen();
            Opacity = 1;
        }
        else
        {
            PositionOnCurrentScreen();
        }

        Topmost = true;
        Activate();
        EnsureForeground();

        FocusSearchBox();
        SelectFirstItemIfNone();
    }

    /// <summary>
    /// 确保面板真的拿到前台焦点。
    /// 由 Win+V 低级键盘钩子唤起时，本进程并不是“最后接收输入的进程”，
    /// Windows 的前台锁定规则会驳回 Activate()/SetForegroundWindow（面板浮起来了却打不了字），
    /// 这里以系统实际的前台窗口为准再强制抢一次。
    /// </summary>
    private void EnsureForeground()
    {
        if (WindowHandle == IntPtr.Zero || NativeMethods.GetForegroundWindow() == WindowHandle)
        {
            return;
        }

        if (PasteService.ForceForeground(WindowHandle))
        {
            Topmost = true;
        }
        else
        {
            Log.Warn("面板未能取得前台焦点（前台窗口可能以管理员权限运行），本次唤起可能无法直接输入");
        }
    }

    /// <summary>隐藏面板（不退出程序）。</summary>
    public void HidePanel()
    {
        _deferredHideTimer.Stop();
        _showInProgress = false;

        if (!IsVisible)
        {
            return;
        }

        PersistWindowSize();
        Hide();
        _host.Main.IsDropHintVisible = false;
    }

    private void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                // 激活是异步完成的（WM_ACTIVATE），这里再确认一次焦点归属
                EnsureForeground();
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
                _showInProgress = false;
            },
            DispatcherPriority.Input);
    }

    private void SelectFirstItemIfNone()
    {
        if (_vm.SelectedItem == null && _vm.Items.Count > 0)
        {
            _vm.SelectedItem = _vm.Items[0];
        }
    }

    /// <summary>把窗口放到鼠标所在显示器的右下角（多显示器 / 高 DPI 均适用）。</summary>
    private void PositionOnCurrentScreen()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                return;
            }

            IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            // 用物理像素计算，避免 DIP/DPI 换算误差
            var dpi = VisualTreeHelper.GetDpi(this);
            double workHeightDip = info.rcWork.Height / dpi.DpiScaleY;
            MaxHeight = Math.Max(320, workHeightDip - 24);

            UpdateLayout();

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
            {
                return;
            }

            const int margin = 12;
            int width = rect.Width;
            int height = rect.Height;

            int x = info.rcWork.Right - width - margin;
            int y = info.rcWork.Bottom - height - margin;

            x = Math.Max(info.rcWork.Left + margin, x);
            y = Math.Max(info.rcWork.Top + margin, y);

            NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Warn("窗口定位失败", ex);
        }
    }

    // ==================================================================
    // 焦点 / 粘贴
    // ==================================================================

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_modalOpen || _isDragging)
        {
            return;
        }

        // 正在弹出面板：抢前台之前的 WM_ACTIVATE(INACTIVE) 不代表用户切走了窗口，
        // 此时隐藏会得到“刚弹出就消失”的面板（由 FocusSearchBox 的延时回调解除该标记）
        if (_showInProgress)
        {
            return;
        }

        if (!_host.SettingsService.Current.HideOnDeactivate)
        {
            return;
        }

        // 右键菜单展开时不要收起面板
        if (HistoryList?.ContextMenu is { IsOpen: true })
        {
            return;
        }

        // 鼠标键按着（很可能正在从别的窗口往这里拖东西）→ 延后隐藏
        if (IsMouseButtonDown())
        {
            _deferredHideTimer.Stop();
            _deferredHideTimer.Start();
            return;
        }

        HidePanel();
    }

    private void OnDeferredHideTick(object? sender, EventArgs e)
    {
        if (IsMouseButtonDown())
        {
            // 还在拖拽中，继续等待
            _deferredHideTimer.Stop();
            _deferredHideTimer.Start();
            return;
        }

        _deferredHideTimer.Stop();
        if (!IsActive && !_isDragging && !_vm.IsDropHintVisible)
        {
            HidePanel();
        }
    }

    /// <summary>是否有鼠标键处于按下状态（用于识别“正在拖拽”）。</summary>
    private static bool IsMouseButtonDown()
    {
        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;
        return (NativeMethods.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
    }

    // ==================================================================
    // 顶部导航胶囊
    // ==================================================================

    /// <summary>
    /// 顶部胶囊被选中时的统一入口。
    ///
    /// 为什么不能只靠属性绑定：点击「全部」时若 `Filter` 本来就是 `All`，属性值没有变化，
    /// `OnFilterChanged` 不会触发 —— 于是"在设置页点全部"就回不到历史列表。
    /// 选中动作本身是可靠的信号，所以页面/筛选的落地放在这里。
    /// </summary>
    private void OnMainChipChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        // 设置项同步会反过来改胶囊的 IsChecked，这里挡掉由此产生的重入
        if (_chipClickInProgress)
        {
            return;
        }

        _chipClickInProgress = true;
        try
        {
            if (tag.StartsWith("history-", StringComparison.Ordinal))
            {
                // 先切回历史页，再选筛选：顺序固定，避免"筛选没变所以页面没切"的老问题
                _vm.SetPageCommand.Execute("history");
                _vm.SetFilterCommand.Execute(tag["history-".Length..]);
            }
            else
            {
                _vm.SetPageCommand.Execute(tag.Equals("emoji", StringComparison.Ordinal) ? "emoji" : "settings");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("切换顶部导航胶囊失败", ex);
        }
        finally
        {
            _chipClickInProgress = false;
        }
    }

    private void OnModalStateChanged(object? sender, bool isOpen) => _modalOpen = isOpen;

    private void OnPasteRequested(object? sender, PasteRequestEventArgs e)
    {
        IntPtr target = _previousForeground;
        HidePanel();

        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            if (!e.RequestPaste)
            {
                _vm.ShowToast($"已复制{e.Description}，按 Ctrl+V 粘贴");
            }

            return;
        }

        if (e.RequestPaste)
        {
            // 自动粘贴：切回原窗口并模拟 Ctrl+V
            PasteService.PasteIntoWindow(target);
        }
        else
        {
            // 只复制：把焦点还给原窗口，用户自己按 Ctrl+V
            PasteService.ActivateWindow(target);
        }
    }

    // ==================================================================
    // 列表交互：单击粘贴 / 拖拽输出
    // ==================================================================

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragMoved = false;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragMoved = true;

        // 从卡片上的按钮按下并拖动时不触发拖拽
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var vm = FindItemViewModel(e.OriginalSource as DependencyObject);
        if (vm == null)
        {
            return;
        }

        _isDragging = true;
        try
        {
            var effects = _host.DragDrop.DoDragDrop(this, vm.Model);
            if (effects != DragDropEffects.None)
            {
                Log.Info($"已拖出内容: {vm.TypeName} -> {effects}");
            }
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void OnListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging || _dragMoved)
        {
            return;
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var vm = FindItemViewModel(e.OriginalSource as DependencyObject);
        if (vm != null)
        {
            _vm.ActivateCommand.Execute(vm);
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static ClipboardItemViewModel? FindItemViewModel(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ListBoxItem item)
            {
                return item.DataContext as ClipboardItemViewModel;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // ==================================================================
    // 外部拖入
    // ==================================================================

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        // 有内容拖到面板上 → 取消“失焦隐藏”，让用户能完成拖放
        _deferredHideTimer.Stop();
        OnWindowDragOver(sender, e);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 内部拖拽（把卡片拖出去）不显示接收提示
        if (_isDragging)
        {
            return;
        }

        bool acceptable = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(DataFormats.Bitmap)
            || e.Data.GetDataPresent("PNG")
            || e.Data.GetDataPresent(DataFormats.UnicodeText);

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (acceptable)
        {
            _vm.IsDropHintVisible = true;
        }
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _vm.IsDropHintVisible = false;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _vm.IsDropHintVisible = false;

        if (_isDragging)
        {
            return;
        }

        e.Handled = true;

        _host.DragDrop.ParseDroppedData(e.Data, content =>
        {
            _ = _host.Main.IngestAsync(content);
            _vm.ShowToast("已加入剪切板历史");
        });
    }

    // ==================================================================
    // 键盘
    // ==================================================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool focusedInTextBox = Keyboard.FocusedElement is TextBoxBase;

        switch (e.Key)
        {
            case Key.Escape:
                HidePanel();
                e.Handled = true;
                break;

            case Key.Enter:
                if (_vm.SelectedItem != null && _vm.IsHistoryPage)
                {
                    _vm.ActivateCommand.Execute(_vm.SelectedItem);
                    e.Handled = true;
                }

                break;

            case Key.Delete:
                if (!focusedInTextBox && _vm.SelectedItem != null && _vm.IsHistoryPage)
                {
                    _vm.DeleteCommand.Execute(_vm.SelectedItem);
                    e.Handled = true;
                }

                break;

            case Key.Down:
                if (focusedInTextBox && _vm.Items.Count > 0)
                {
                    MoveSelection(1);
                    e.Handled = true;
                }

                break;

            case Key.Up:
                if (focusedInTextBox && _vm.Items.Count > 0)
                {
                    MoveSelection(-1);
                    e.Handled = true;
                }

                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int index = _vm.SelectedItem == null ? -1 : _vm.Items.IndexOf(_vm.SelectedItem);
        int target = Math.Clamp(index + delta, 0, _vm.Items.Count - 1);
        _vm.SelectedItem = _vm.Items[target];
        HistoryList.ScrollIntoView(_vm.SelectedItem);
    }

    /// <summary>不在输入框里打字时，直接把字符送进搜索框。</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        _vm.SearchText += e.Text;
        FocusSearchBox();
        e.Handled = true;
    }

    // ==================================================================
    // 其它按钮
    // ==================================================================

    private void OnHideClick(object sender, RoutedEventArgs e) => HidePanel();

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _vm.MonitorPaused = !_vm.MonitorPaused;
        _host.Monitor.IsPaused = _vm.MonitorPaused;
        _host.Tray?.SyncState(_host.SettingsService.Current);
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: EmojiEntry entry })
        {
            _vm.Emoji.Pick(entry);
        }
    }
}
