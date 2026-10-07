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
using LightClipboard.Models;
using LightClipboard.Selection;
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

    /// <summary>多选（仅图片）状态机，构造函数里用 <c>_vm.Items</c> 初始化。</summary>
    private readonly ClipboardSelection _selection;

    /// <summary>
    /// “按下鼠标那一刻”的修饰键快照：整个手势（含 MouseUp 分派）都以它为准。
    ///
    /// 为什么不能在 MouseUp 里读 <see cref="Keyboard.Modifiers"/>：用户按下 Ctrl 后、
    /// 松开鼠标左键之前先松开 Ctrl，就会被误判成“普通单击”，于是**清空多选并且顺手粘贴一次**。
    /// </summary>
    private ModifierKeys _gestureModifiers;

    /// <summary>最近一次"按 Esc 取消拖拽"的时间戳（TickCount64），用于吞掉紧随其后的那个 Esc。</summary>
    private long _dragCancelledByEscTick;

    /// <summary>吞掉"取消拖拽的那个 Esc"的时间窗口（毫秒）。</summary>
    private const long DragCancelEscGraceMs = 400;

    public MainWindow(AppHost host)
    {
        _host = host;
        _vm = host.Main;

        InitializeComponent();

        DataContext = _vm;

        // 多选状态机：本身不持久化，纯“面板内的一次交互状态”，
        // 列表整体重建（Reset）时会自动清空，见 ClipboardSelection。
        _selection = new ClipboardSelection(_vm.Items);
        _selection.Changed += (_, _) => UpdateSelectionHint();

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

    /// <summary>
    /// 显示面板并把键盘焦点交给**窗口本身**（不聚焦搜索框，见 <see cref="FocusPanelWithoutCaret"/>）。
    /// </summary>
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
        // 解除动作放在 FocusPanelWithoutCaret 的延时回调里（等激活消息处理完）。
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

        // 「始终置顶」：呼出时也强制抬一次 —— Topmost 本来就是 true，再赋一次值不会触发
        // SetWindowPos，此刻若正被另一个置顶窗口压着，光靠属性赋值是抬不回来的
        ReassertTopmostIfPinned();

        Activate();
        EnsureForeground();

        FocusPanelWithoutCaret();
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

        // 面板是临时弹窗，收起后不留残留选中态（否则下次呼出会看到"幽灵多选"）。
        // 放在 IsVisible 判定之前：已经隐藏时也要把选择清干净。
        _selection.Clear();

        if (!IsVisible)
        {
            return;
        }

        PersistWindowSize();
        Hide();
        _host.Main.IsDropHintVisible = false;
    }

    /// <summary>
    /// 拖出成功后的收尾：把卡片拖到别的程序并被对方接收时收起面板。
    ///
    /// 为什么需要单独一个方法：拖拽开始后本窗口会失焦，但 <see cref="OnWindowDeactivated"/>
    /// 为了不打断拖拽会把这次失焦直接丢掉（`_isDragging` 分支），而 OLE 拖拽循环结束后
    /// 不会再有第二次失焦事件 —— 没有这里补一次，面板就会一直停在屏幕上。
    ///
    /// 只在目标程序真的接收（`DragDropEffects != None`）时隐藏：取消拖拽（Esc / 松在空白处）
    /// 保留面板，避免用户"拖了一下没成"还要重新呼出。
    /// 开关沿用设置页的「失焦即隐藏」：关掉它表示用户希望面板常驻，拖出同样不该收起。
    /// </summary>
    private void HideAfterDragOut()
    {
        if (!_host.SettingsService.Current.HideOnDeactivate)
        {
            Log.Info("拖出成功，但「失焦即隐藏」已关闭，面板保持显示");
            return;
        }

        Log.Info("拖出成功，面板已自动隐藏");
        HidePanel();
    }

    /// <summary>
    /// 本次拖拽的松手位置是否仍落在面板窗口内（真机实测：内部拖拽也会返回 Copy，
    /// 因为面板自己是合法放置目标，WPF 会把 allowedEffects 原样当作结果返回）。
    ///
    /// 取松手时的真实光标位置与窗口矩形比较，两点都用物理像素（与 <see cref="PositionOnCurrentScreen"/>
    /// 同一套坐标口径），多显示器 / 高 DPI 下不会错位。
    /// </summary>
    private bool IsDropInsidePanel()
    {
        if (WindowHandle == IntPtr.Zero
            || !NativeMethods.GetCursorPos(out var cursor)
            || !NativeMethods.GetWindowRect(WindowHandle, out var rect))
        {
            // 查询失败时按"在面板外"处理：与"目标程序已接收"的主路径保持一致
            return false;
        }

        return cursor.X >= rect.Left && cursor.X < rect.Right
            && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    /// <summary>
    /// 「始终置顶」：面板失焦后，把它**无焦点**地抬回置顶层最上方。
    ///
    /// 为什么需要它：面板本身就是 Topmost（XAML + ShowPanel 都设了），**普通窗口压不住它**；
    /// 能压住它的是"同样置顶"的窗口（全屏/最大化浏览器、播放器、其它置顶工具）—— Windows 的规则是
    /// "置顶层里最后被激活的那个在最上面"，于是用户一点击那个窗口，面板就落到它下面，
    /// 看起来像"面板自己消失了"（真机实测：点击后"面板中心处最上层窗口"从面板变成了浏览器）。
    ///
    /// 两个前提，缺一不可：
    /// - 开关打开（默认关闭，见 AppSettings.KeepOnTopWhenUnfocused）；
    /// - 「失焦即隐藏」关闭 —— 否则面板失焦就直接收起了，谈不上被谁挡住（设置页里这一项也从属于它）。
    ///
    /// 只抬层级、绝不抢焦点：SetWindowPos 带 SWP_NOACTIVATE，否则用户点别的窗口会被面板弹回来。
    /// </summary>
    private void ReassertTopmostIfPinned()
    {
        var settings = _host.SettingsService.Current;
        if (!settings.KeepOnTopWhenUnfocused
            || settings.HideOnDeactivate
            || WindowHandle == IntPtr.Zero
            || !IsVisible)
        {
            return;
        }

        RaiseTopmost();

        // 再排一次：对方的激活动作可能在本条消息之后才走完 z 序调整（重复抬一次没有副作用）
        Dispatcher.BeginInvoke(RaiseTopmost, DispatcherPriority.Background);
        Log.Info("「始终置顶」：失焦后已把面板抬回最上层");
    }

    /// <summary>把面板放到置顶层最上方，不移动、不改变大小、不抢焦点。</summary>
    private void RaiseTopmost()
    {
        if (WindowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            WindowHandle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 呼出面板时把键盘焦点交给窗口本身，**不聚焦任何控件**：
    /// 搜索框里不会出现闪烁的光标，"呼出 = 进入输入模式"的错觉随之消失。
    /// 窗口持有焦点即可保证键盘照常可用 —— Esc / Enter / Delete / ↑↓ / Ctrl+F 全部走
    /// 窗口级的 <see cref="OnPreviewKeyDown"/>，不依赖子控件焦点（v1.2.2 起）。
    /// </summary>
    private void FocusPanelWithoutCaret()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                // 激活是异步完成的（WM_ACTIVATE），这里再确认一次焦点归属
                EnsureForeground();

                // 清掉可能残留的子控件逻辑焦点，再把键盘焦点交给窗口自己。
                // 只调 Keyboard.ClearFocus() 会让 FocusedElement 变成 null，
                // 那时输入路由没有落点，Esc / Enter 都会失灵，所以必须显式聚焦窗口。
                FocusManager.SetFocusedElement(this, null);
                Focus();
                Keyboard.Focus(this);
                _showInProgress = false;
            },
            DispatcherPriority.Input);
    }

    /// <summary>把光标放进搜索框（Ctrl+F 或用户自己点击搜索框时走这里）。</summary>
    private void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                EnsureForeground();
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();   // 已有关键词时全选，方便直接重打
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
        // 此时隐藏会得到“刚弹出就消失”的面板（由 FocusPanelWithoutCaret 的延时回调解除该标记）
        if (_showInProgress)
        {
            return;
        }

        if (!_host.SettingsService.Current.HideOnDeactivate)
        {
            // 面板要常驻：顺手处理"被同样置顶的窗口压住"（见 ReassertTopmostIfPinned）
            ReassertTopmostIfPinned();
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

        Log.Info("失焦，面板已自动隐藏（设置项「失焦即隐藏」开启时才会发生）");
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

        // 「失焦即隐藏」必须在这里再查一次：计时器可能是**设置还开着的时候**启动的
        // （隐藏延迟 50–1000ms 之间用户完全来得及把它关掉），而它一旦到点就无条件收起面板，
        // 于是表现为"开关关了还是照样隐藏"。所有自动收起路径都必须服从这个开关。
        if (!_host.SettingsService.Current.HideOnDeactivate)
        {
            return;
        }

        if (!IsActive && !_isDragging && !_vm.IsDropHintVisible)
        {
            Log.Info("延迟隐藏到点，面板已自动隐藏（设置项「失焦即隐藏」开启时才会发生）");
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
            // 没粘上：内容留在剪切板里让用户自己按 Ctrl+V —— 这次输出不算"借"，不做还原
            _host.Writer.DiscardRestore();

            if (!e.RequestPaste)
            {
                _vm.ShowToast($"已复制{e.Description}，按 Ctrl+V 粘贴");
            }

            return;
        }

        if (e.RequestPaste)
        {
            // 自动粘贴：切回原窗口并模拟 Ctrl+V；按键送达后把剪切板还原成输出前的内容
            // （"借一次就还"：用户随后按 Ctrl+V 得到的仍是输出前那份，通常就是列表顶部那条）
            PasteService.PasteIntoWindow(target, _host.Writer.ScheduleRestoreAfterPaste);
        }
        else
        {
            // 只复制：把焦点还给原窗口，用户自己按 Ctrl+V。
            // 这条路径**刻意不还原** —— 它的语义就是把这一条留在剪切板里。
            _host.Writer.DiscardRestore();
            PasteService.ActivateWindow(target);
        }
    }

    // ==================================================================
    // 列表交互：单击粘贴 / 拖拽输出
    //
    // 多选（仅图片）四条铁律（一页速查，改动前先读这里）：
    // 1. 修饰键只在 MouseDown 时读一次，存进 _gestureModifiers；MouseUp 用快照分派。
    //    理由：松手前先松开 Ctrl 会被误判成"普通单击 → 清空多选 + 顺手粘贴一次"。
    // 2. 修饰键分支里绝不调用 ActivateCommand / 不碰剪切板 / 不隐藏面板。
    // 3. Ctrl/Shift 按下时不启动拖拽（守卫放在位移判定之前）。
    // 4. 拖拽收尾（HideAfterDragOut / IsDropInsidePanel）只换入参、不换逻辑 ——
    //    它是 04 篇不可回退项，删掉后"拖到一半没成"或"面板内手抖拖一下"都会收起面板。
    // ==================================================================

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragMoved = false;

        // 记下"按下这一刻"的修饰键，整个手势都以它为准（见字段上的注释）
        _gestureModifiers = Keyboard.Modifiers;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // Ctrl / Shift 按下的手势是"选择"，不启动拖拽。
        // 放在位移判定之前：手势被干净地分成三种（修饰键+点击=选择、
        // 无修饰键+点击=激活、无修饰键+拖动=拖出），DoDragDrop 的收尾逻辑一个字都不用改。
        // 注意：修饰键一旦在这个手势期间按下就不拖了，这是有意的（见方案 §2.1）。
        if ((_gestureModifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
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
            // 已选中的卡片 → 拖出整批（按拖出**队列顺序**：有 Ctrl 参与 = 点击顺序，纯 Shift = 列表顺序）；
            // 未选中的卡片 → 仍只拖这一张（与资源管理器一致）。
            var dragItems = _selection.IsSelected(vm)
                ? _selection.BuildDragItems()
                : new List<ClipboardItem> { vm.Model };

            if (dragItems.Count == 0)
            {
                // 兜底：选中项全被过滤掉了（例如图片路径为空），退回单张路径
                dragItems = new List<ClipboardItem> { vm.Model };
            }

            // count == 1 走原路径（Bitmap + PNG 单张语义），单张行为完全不变
            var effects = dragItems.Count > 1
                ? _host.DragDrop.DoDragDrop(this, dragItems)
                : _host.DragDrop.DoDragDrop(this, dragItems[0]);

            // 拖拽刚结束时 Esc 仍按着 → 这次是"用 Esc 取消的拖拽"。
            // OLE 通常会用掉那次按键来取消拖拽（正常情况下 Esc 到不了窗口），但真机上偶发漏网：
            // 6 用例回归里出现过一次"取消拖拽后面板被顺手收起"。这里记下时间戳，
            // 由 OnPreviewKeyDown 的 Esc 分支在很短的窗口内吞掉它 —— 只在 Esc 仍按着时才生效，
            // 因此不影响"平时按 Esc 收起面板"。
            if (Keyboard.IsKeyDown(Key.Escape))
            {
                _dragCancelledByEscTick = Environment.TickCount64;
            }

            if (effects != DragDropEffects.None)
            {
                Log.Info($"已拖出内容: {vm.TypeName} -> {effects}");

                // 拖出成功（目标程序真的接下了内容）→ 面板自动收起，与"点击粘贴后隐藏"保持一致。
                // 必须在这里补偿：拖拽期间 OnWindowDeactivated 会因 _isDragging 直接返回，
                // 拖拽结束后也不会再有失焦事件，没有这一步面板就会一直留在屏幕上。
                // 松手位置仍在面板内时不算"拖出"（内部拖拽同样会返回 Copy，见 IsDropInsidePanel）。
                if (IsDropInsidePanel())
                {
                    Log.Info("拖拽在面板内结束，面板保持显示");
                }
                else
                {
                    HideAfterDragOut();
                }
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
        if (vm == null)
        {
            return;
        }

        // 按"按下时的修饰键快照"分派：修饰键点击 = 纯选择，不复制、不粘贴、不收面板。
        // 判断依据只能是 _gestureModifiers，不能是 Keyboard.Modifiers（见字段注释）。
        // 也**刻意不设 e.Handled**：本方法是 PreviewMouseLeftButtonUp（隧道路由），
        // 在这里吞掉事件会让 ListBoxItem 收不到自己的 MouseUp，
        // 进而可能留下未释放的鼠标捕获，把后续点击一起弄坏。
        if ((_gestureModifiers & ModifierKeys.Control) != 0)
        {
            if (!_selection.ToggleImage(vm))
            {
                _vm.ShowToast("多选仅支持图片");
            }

            return;
        }

        if ((_gestureModifiers & ModifierKeys.Shift) != 0)
        {
            _selection.SelectRangeImages(vm);
            return;
        }

        // 普通单击：清空多选 + **原样**走激活（复制 / 自动粘贴）
        _selection.SelectSingleImage(vm);
        _vm.ActivateCommand.Execute(vm);
    }

    /// <summary>
    /// 刷新页脚的"已选 N 张图片"提示（空串时整行隐藏，避免空 TextBlock 白占一行高度）。
    /// MainWindow 不是 ObservableObject，这一处提示直接写控件属性，不引入新的绑定层。
    /// </summary>
    private void UpdateSelectionHint()
    {
        if (SelectionHint == null)
        {
            return;
        }

        string text = _selection.SelectionText;
        SelectionHint.Text = text;
        SelectionHint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
                // 紧跟"Esc 取消拖拽"之后到来的同一个 Esc 要吞掉，别顺手把面板也收起
                if (Environment.TickCount64 - _dragCancelledByEscTick < DragCancelEscGraceMs)
                {
                    Log.Info("忽略紧随拖拽取消的 Esc，面板保持显示");
                    _dragCancelledByEscTick = 0;
                    e.Handled = true;
                    break;
                }

                HidePanel();
                e.Handled = true;
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                // 呼出后光标不在搜索框里（v1.2.2），Ctrl+F 是纯键盘用户进入搜索的入口
                FocusSearchBox();
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

            // ↑↓ 翻列表：焦点在窗口上（没有子控件聚焦）时也要能用，所以不再要求 focusedInTextBox
            case Key.Down:
                if (_vm.Items.Count > 0)
                {
                    MoveSelection(1);
                    e.Handled = true;
                }

                break;

            case Key.Up:
                if (_vm.Items.Count > 0)
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
