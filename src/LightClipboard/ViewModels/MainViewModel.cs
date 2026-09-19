using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightClipboard.Data;
using LightClipboard.Models;
using LightClipboard.Services;

namespace LightClipboard.ViewModels;

/// <summary>历史列表筛选条件。</summary>
public enum HistoryFilter
{
    All = 0,
    Text = 1,
    Image = 2,
    Files = 3,
    Pinned = 4,
}

/// <summary>主面板页面。</summary>
public enum MainPage
{
    History = 0,
    Emoji = 1,
    Settings = 2,
}

/// <summary>请求“复制并粘贴”的事件参数。</summary>
public sealed class PasteRequestEventArgs : EventArgs
{
    public PasteRequestEventArgs(ClipboardItem? item, bool requestPaste, string description)
    {
        Item = item;
        RequestPaste = requestPaste;
        Description = description;
    }

    public ClipboardItem? Item { get; }

    public bool RequestPaste { get; }

    public string Description { get; }
}

/// <summary>
/// 主面板视图模型：负责历史列表的装载、筛选、搜索，以及复制/粘贴/删除/收藏等操作。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly StorageService _storage;
    private readonly ImageCacheManager _images;
    private readonly ClipboardWriter _writer;
    private readonly SettingsService _settingsService;
    private readonly KeyboardHookService _keyboardHook;
    private readonly SemaphoreSlim _ingestGate = new(1, 1);
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _statusTimer;

    private readonly List<ClipboardItemViewModel> _all = new();
    private readonly Dictionary<int, ClipboardItemViewModel> _cache = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private HistoryFilter _filter = HistoryFilter.All;

    [ObservableProperty]
    private MainPage _page = MainPage.History;

    [ObservableProperty]
    private ClipboardItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private bool _isDropHintVisible;

    [ObservableProperty]
    private string _hotkeyText = "Ctrl+Shift+V";

    public MainViewModel(
        StorageService storage,
        ImageCacheManager images,
        ClipboardWriter writer,
        SettingsService settingsService,
        KeyboardHookService keyboardHook)
    {
        _storage = storage;
        _images = images;
        _writer = writer;
        _settingsService = settingsService;
        _keyboardHook = keyboardHook;

        Emoji = new EmojiViewModel();
        Emoji.Picked += OnEmojiPicked;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _settingsService.Save();
        };
    }

    /// <summary>要求宿主窗口执行“隐藏并粘贴”。</summary>
    public event EventHandler<PasteRequestEventArgs>? PasteRequested;

    /// <summary>打开/关闭模态对话框（用于抑制“失焦自动隐藏”）。</summary>
    public event EventHandler<bool>? ModalStateChanged;

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    public EmojiViewModel Emoji { get; }

    public AppSettings Settings => _settingsService.Current;

    public bool HasItems => Items.Count > 0;

    public string EmptyHint => string.IsNullOrWhiteSpace(SearchText)
        ? "还没有历史记录。\n复制任意文本、Emoji 或图片（Win+Shift+S 截图）即可自动收录。"
        : "没有匹配的记录，换个关键词试试。";

    // ------------------------------------------------------------------
    // 设置项（双向绑定）
    // ------------------------------------------------------------------

    public bool AutoPaste
    {
        get => Settings.AutoPaste;
        set
        {
            if (Settings.AutoPaste == value)
            {
                return;
            }

            _settingsService.Update(s => s.AutoPaste = value);
            OnPropertyChanged();
        }
    }

    public bool HideOnDeactivate
    {
        get => Settings.HideOnDeactivate;
        set
        {
            if (Settings.HideOnDeactivate == value)
            {
                return;
            }

            _settingsService.Update(s => s.HideOnDeactivate = value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 失焦后的隐藏缓冲时长（毫秒）。窗口侧订阅 SettingsService.Changed 实时套用新的计时器间隔，
    /// 这里只负责数值的钳制与展示。
    /// </summary>
    public int DeferredHideDelayMs
    {
        get => Settings.DeferredHideDelayMs;
        set
        {
            int clamped = Math.Clamp(
                value,
                SettingsService.MinDeferredHideDelayMs,
                SettingsService.MaxDeferredHideDelayMs);

            if (Settings.DeferredHideDelayMs == clamped)
            {
                // 即使数值没变也要刷新输入框：手动输入 20 / 9999 这类越界值时，输入框要回弹到合法值
                OnPropertyChanged(nameof(DeferredHideDelayInput));
                return;
            }

            _settingsService.Update(s => s.DeferredHideDelayMs = clamped);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeferredHideDelayInput));
        }
    }

    /// <summary>文本框手动输入：允许空串/非法输入，落焦点时统一解析并钳制。</summary>
    public string DeferredHideDelayInput
    {
        get => DeferredHideDelayMs.ToString();
        set
        {
            if (int.TryParse(value?.Trim(), out int parsed))
            {
                DeferredHideDelayMs = parsed;
            }
            else
            {
                // 非法输入直接回弹到当前值
                OnPropertyChanged();
            }
        }
    }

    public bool MonitorPaused
    {
        get => Settings.MonitorPaused;
        set
        {
            if (Settings.MonitorPaused == value)
            {
                return;
            }

            _settingsService.Update(s => s.MonitorPaused = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MonitoringStateText));
            OnPropertyChanged(nameof(PauseGlyph));
            OnPropertyChanged(nameof(MonitoringHint));
            ShowToast(value ? "已暂停监听剪切板" : "已恢复监听剪切板");
        }
    }

    /// <summary>
    /// 是否接管原生 Win+V。写入设置后由 AppHost 统一启动/停止低级键盘钩子
    /// （订阅了 SettingsService.Changed），这里只负责刷新界面文案。
    /// </summary>
    public bool WinVHookEnabled
    {
        get => Settings.EnableWinVHook;
        set
        {
            if (Settings.EnableWinVHook == value)
            {
                return;
            }

            _settingsService.Update(s => s.EnableWinVHook = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(WinVHookStatusText));
            OnPropertyChanged(nameof(MonitoringHint));
            ShowToast(value ? "已接管 Win+V，系统剪切板历史不再弹出" : "已交还 Win+V，恢复系统原生剪切板历史");
        }
    }

    /// <summary>Win+V 接管状态说明（安装失败时提示查看日志，而不是静默失效）。</summary>
    public string WinVHookStatusText
    {
        get
        {
            if (!Settings.EnableWinVHook)
            {
                return "已关闭：Win+V 仍是系统原生剪切板历史";
            }

            return _keyboardHook.IsRunning
                ? "已接管：按 Win+V 直接唤出本面板，不再弹出系统剪切板历史"
                : "接管失败：低级键盘钩子未能安装，详见日志（可能被安全软件拦截）";
        }
    }

    public string MonitoringStateText => MonitorPaused ? "监听已暂停" : "正在监听剪切板";

    /// <summary>暂停/恢复按钮的图标字形（Segoe Fluent Icons）。</summary>
    public string PauseGlyph => MonitorPaused ? "\uE768" : "\uE769";

    /// <summary>页脚提示。</summary>
    public string MonitoringHint => MonitorPaused
        ? "监听已暂停"
        : Settings.EnableWinVHook
            ? $"Win+V / {HotkeyText} 唤起 · 点击卡片粘贴 · 拖拽卡片可发送图片"
            : $"{HotkeyText} 唤起 · 点击卡片粘贴 · 拖拽卡片可发送图片";

    public bool RunAtStartup
    {
        get => StartupService.IsEnabled();
        set
        {
            if (StartupService.SetEnabled(value))
            {
                _settingsService.Update(s => s.RunAtStartup = value);
            }

            OnPropertyChanged();
            ShowToast(value ? "已设置开机自动启动" : "已取消开机自动启动");
        }
    }

    public AppThemeMode ThemeMode
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value)
            {
                return;
            }

            _settingsService.Update(s => s.Theme = value);
            ThemeService.Apply(value);
            if (value == AppThemeMode.System)
            {
                ThemeService.WatchSystemTheme(System.Windows.Application.Current.MainWindow);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeModeIndex));
        }
    }

    /// <summary>0=跟随系统 1=浅色 2=深色（供 ComboBox 绑定）。</summary>
    public int ThemeModeIndex
    {
        get => (int)ThemeMode;
        set
        {
            if (value is >= 0 and <= 2)
            {
                ThemeMode = (AppThemeMode)value;
            }
        }
    }

    public int MaxItems
    {
        get => Settings.MaxItems;
        set
        {
            int clamped = Math.Clamp(value, 20, 2000);
            if (Settings.MaxItems == clamped)
            {
                return;
            }

            _settingsService.Update(s => s.MaxItems = clamped);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaxItemsText));
            TrimToLimit();
        }
    }

    public string MaxItemsText => $"{MaxItems} 条";

    public string DataDirectory => AppPaths.Root;

    public string VersionText
    {
        get
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string ItemCountText
    {
        get
        {
            int pinned = _all.Count(x => x.IsPinned);
            return pinned > 0
                ? $"共 {_all.Count} 条 · 收藏 {pinned} 条"
                : $"共 {_all.Count} 条";
        }
    }

    // ------------------------------------------------------------------
    // 装载与刷新
    // ------------------------------------------------------------------

    /// <summary>从数据库装载全部历史。</summary>
    public void Load()
    {
        _all.Clear();
        _cache.Clear();

        foreach (var item in _storage.GetAll())
        {
            var vm = new ClipboardItemViewModel(item);
            _all.Add(vm);
            _cache[item.Id] = vm;
        }

        RebuildView();
        StatusText = MonitoringStateText;
    }

    /// <summary>重新按筛选条件生成可见列表（会复用已有的卡片视图模型）。</summary>
    private void RebuildView()
    {
        Items.Clear();
        foreach (var vm in _all.Where(PassesFilter))
        {
            Items.Add(vm);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(ItemCountText));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private bool PassesFilter(ClipboardItemViewModel vm)
    {
        bool filterOk = Filter switch
        {
            HistoryFilter.Text => vm.IsText,
            HistoryFilter.Image => vm.IsImage,
            HistoryFilter.Files => vm.IsFiles,
            HistoryFilter.Pinned => vm.IsPinned,
            _ => true,
        };

        return filterOk && vm.Matches(SearchText.Trim());
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildView();
        Emoji.ApplySearch(value);
    }

    partial void OnHotkeyTextChanged(string value) => OnPropertyChanged(nameof(MonitoringHint));

    /// <summary>
    /// 顶部筛选胶囊（全部 / 文本 / 图片 / 文件 / ★）变化。
    /// 它同时承担“回到历史页”的语义：在 Emoji / 设置页点任意筛选都会切回历史列表，
    /// 因此“全部”就是完整历史列表，不再需要单独的“历史”按钮。
    /// </summary>
    partial void OnFilterChanged(HistoryFilter value)
    {
        RebuildView();

        if (Page != MainPage.History)
        {
            Page = MainPage.History;
        }
    }

    partial void OnPageChanged(MainPage value)
    {
        // 切换页面时清空搜索框：避免把历史搜索词带进 Emoji 页（或反之）导致“看起来是空的”
        if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
        }

        Emoji.ApplySearch(value == MainPage.Emoji ? SearchText : string.Empty);

        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsEmojiPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    public bool IsHistoryPage => Page == MainPage.History;

    public bool IsEmojiPage => Page == MainPage.Emoji;

    public bool IsSettingsPage => Page == MainPage.Settings;

    // ------------------------------------------------------------------
    // 剪切板内容入库
    // ------------------------------------------------------------------

    /// <summary>
    /// 把一份解析后的剪切板内容写入历史（含图片落盘、去重与超限裁剪）。
    /// </summary>
    public async Task IngestAsync(ParsedClipboardContent content, bool moveToTop = true)
    {
        if (content == null)
        {
            return;
        }

        await _ingestGate.WaitAsync().ConfigureAwait(true);
        try
        {
            ClipboardItem? item = content.Type switch
            {
                ClipboardItemType.Text => BuildTextItem(content),
                ClipboardItemType.Image => await BuildImageItemAsync(content).ConfigureAwait(true),
                ClipboardItemType.Files => BuildFilesItem(content),
                _ => null,
            };

            if (item == null)
            {
                return;
            }

            var (saved, isNew) = _storage.AddOrTouch(item);

            if (isNew)
            {
                Log.Info($"新增剪切板记录: {saved.Type} / {saved.Hash[..Math.Min(8, saved.Hash.Length)]}");
                ApplyToView(saved, moveToTop: true, isNew: true);
                TrimToLimit();
            }
            else
            {
                ApplyToView(saved, moveToTop, isNew: false);
            }

            OnPropertyChanged(nameof(ItemCountText));
        }
        catch (Exception ex)
        {
            Log.Error("写入剪切板历史失败", ex);
        }
        finally
        {
            _ingestGate.Release();
        }
    }

    private ClipboardItem? BuildTextItem(ParsedClipboardContent content)
    {
        string? text = content.Text;
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            Text = text,
            Hash = ClipboardWriter.ComputeTextHash(text),
            ByteSize = Encoding.UTF8.GetByteCount(text),
            SourceApp = content.SourceApp,
            CreatedAt = DateTime.Now,
            LastUsedAt = DateTime.Now,
        };
    }

    private async Task<ClipboardItem?> BuildImageItemAsync(ParsedClipboardContent content)
    {
        var bitmap = content.Image;
        if (bitmap == null)
        {
            return null;
        }

        // 关键：先在 UI 线程把位图规范化成完全冻结的纯像素位图。
        // 剪切板取回的 BitmapFrame 内部仍绑定 UI 线程的解码器，即便 Freeze() 过，
        // 在线程池里编码也会抛 InvalidOperationException。
        BitmapSource frozen;
        try
        {
            frozen = ImageCacheManager.Normalize(bitmap, content.PreserveAlpha);
        }
        catch (Exception ex)
        {
            Log.Warn("规范化位图失败，直接使用原位图", ex);
            frozen = bitmap;
        }

        string path;
        string hash;
        long size;

        try
        {
            // PNG 编码 + 落盘放到后台线程，避免大截图阻塞 UI
            (path, hash, size) = await Task.Run(() => _images.SavePng(frozen)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("后台保存图片失败，改为同步保存", ex);
            (path, hash, size) = _images.SavePng(frozen);
        }

        return new ClipboardItem
        {
            Type = ClipboardItemType.Image,
            ImagePath = path,
            ImageWidth = frozen.PixelWidth,
            ImageHeight = frozen.PixelHeight,
            Hash = hash,
            ByteSize = size,
            SourceApp = content.SourceApp,
            CreatedAt = DateTime.Now,
            LastUsedAt = DateTime.Now,
        };
    }

    private static ClipboardItem? BuildFilesItem(ParsedClipboardContent content)
    {
        var paths = content.FilePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return null;
        }

        long size = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    size += new FileInfo(path).Length;
                }
            }
            catch
            {
                // 忽略无法访问的文件
            }
        }

        return new ClipboardItem
        {
            Type = ClipboardItemType.Files,
            FilePaths = paths,
            Hash = ClipboardWriter.ComputeFilesHash(paths),
            ByteSize = size,
            SourceApp = content.SourceApp,
            CreatedAt = DateTime.Now,
            LastUsedAt = DateTime.Now,
        };
    }

    /// <summary>把一条落库记录同步到界面列表（新建、置顶或刷新）。</summary>
    private void ApplyToView(ClipboardItem item, bool moveToTop, bool isNew)
    {
        if (!_cache.TryGetValue(item.Id, out var vm))
        {
            vm = new ClipboardItemViewModel(item);
            _cache[item.Id] = vm;
            _all.Add(vm);
            isNew = true;
        }
        else
        {
            vm.Update(item);
        }

        if (!isNew)
        {
            _all.Remove(vm);
            _all.Add(vm);
        }

        SortList();
        SyncVisible(vm);
        OnPropertyChanged(nameof(ItemCountText));
    }

    private void SortList()
    {
        _all.Sort(static (a, b) =>
        {
            if (a.IsPinned != b.IsPinned)
            {
                return a.IsPinned ? -1 : 1;
            }

            return b.LastUsedAt.CompareTo(a.LastUsedAt);
        });
    }

    /// <summary>把单个卡片同步进可见列表，尽量只做插入/移动，避免整表重建。</summary>
    private void SyncVisible(ClipboardItemViewModel vm)
    {
        bool visible = PassesFilter(vm);
        int currentIndex = Items.IndexOf(vm);

        if (!visible)
        {
            if (currentIndex >= 0)
            {
                Items.RemoveAt(currentIndex);
            }

            return;
        }

        int target = 0;
        foreach (var candidate in _all)
        {
            if (ReferenceEquals(candidate, vm))
            {
                break;
            }

            if (PassesFilter(candidate))
            {
                target++;
            }
        }

        if (currentIndex < 0)
        {
            Items.Insert(Math.Min(target, Items.Count), vm);
        }
        else if (currentIndex != target)
        {
            Items.Move(currentIndex, Math.Min(target, Items.Count - 1));
        }

        OnPropertyChanged(nameof(HasItems));
    }

    private void TrimToLimit()
    {
        var removed = _storage.Prune(Settings.MaxItems);
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var item in removed)
        {
            if (_cache.Remove(item.Id, out var vm))
            {
                _all.Remove(vm);
                Items.Remove(vm);
            }

            if (item.Type == ClipboardItemType.Image)
            {
                _images.DeleteImageFile(item.ImagePath);
            }
        }

        Log.Info($"历史超限，已清理 {removed.Count} 条记录");
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(HasItems));
    }

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    /// <summary>点击卡片：复制到剪切板（并按设置请求自动粘贴）。</summary>
    [RelayCommand]
    private void Activate(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        CopyToClipboard(vm, requestPaste: AutoPaste);
    }

    /// <summary>仅复制，不粘贴。</summary>
    [RelayCommand]
    private void Copy(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        CopyToClipboard(vm, requestPaste: false);
        ShowToast($"已复制{vm.TypeName}");
    }

    /// <summary>复制并立即粘贴。</summary>
    [RelayCommand]
    private void Paste(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        CopyToClipboard(vm, requestPaste: true);
    }

    private void CopyToClipboard(ClipboardItemViewModel vm, bool requestPaste)
    {
        try
        {
            bool ok = _writer.SetItem(vm.Model, Settings.CopyImageWithFilePath);
            if (!ok)
            {
                ShowToast("复制失败：剪切板被其他程序占用");
                return;
            }

            _storage.Touch(vm.Id);
            var touched = _storage.GetById(vm.Id);
            if (touched != null)
            {
                vm.Update(touched);
                _all.Remove(vm);
                _all.Add(vm);
                SortList();
                SyncVisible(vm);
                SelectedItem = vm;
            }

            PasteRequested?.Invoke(this, new PasteRequestEventArgs(vm.Model, requestPaste, vm.TypeName));
        }
        catch (Exception ex)
        {
            Log.Error("复制历史条目失败", ex);
            ShowToast("复制失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void TogglePin(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        _storage.SetPinned(vm.Id, !vm.IsPinned);
        var updated = _storage.GetById(vm.Id);
        if (updated == null)
        {
            return;
        }

        vm.Update(updated);
        SortList();
        RebuildView();
        ShowToast(vm.IsPinned ? "已收藏，不会被自动清理" : "已取消收藏");
    }

    [RelayCommand]
    private void Delete(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        var removed = _storage.Delete(vm.Id);
        if (removed == null)
        {
            return;
        }

        if (removed.Type == ClipboardItemType.Image)
        {
            _images.DeleteImageFile(removed.ImagePath);
        }

        _cache.Remove(vm.Id);
        _all.Remove(vm);
        Items.Remove(vm);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ItemCountText));
        ShowToast("已删除该记录");
    }

    [RelayCommand]
    private void ClearAll()
    {
        System.Windows.MessageBoxResult result;
        ModalStateChanged?.Invoke(this, true);
        try
        {
            result = System.Windows.MessageBox.Show(
                "确定要清空全部历史记录吗？\n（收藏的记录会被保留）",
                "LightClipboard",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
        }
        finally
        {
            ModalStateChanged?.Invoke(this, false);
        }

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _storage.Clear(keepPinned: true);
        foreach (var item in removed)
        {
            if (item.Type == ClipboardItemType.Image)
            {
                _images.DeleteImageFile(item.ImagePath);
            }
        }

        Load();
        _storage.Shrink();
        ShowToast($"已清空 {removed.Count} 条记录");
    }

    [RelayCommand]
    private void Refresh() => Load();

    /// <summary>另存为文件（文本 → .txt，图片 → .png，文件条目 → 复制到目标目录）。</summary>
    [RelayCommand]
    private void SaveAs(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        ModalStateChanged?.Invoke(this, true);
        try
        {
            switch (vm.Type)
            {
                case ClipboardItemType.Text:
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "另存为文本文件",
                        FileName = $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                        Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                        DefaultExt = ".txt",
                    };

                    if (dialog.ShowDialog() == true)
                    {
                        File.WriteAllText(dialog.FileName, vm.Model.Text ?? string.Empty, new UTF8Encoding(true));
                        ShowToast("已保存文本文件");
                    }

                    break;
                }

                case ClipboardItemType.Image:
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "另存为图片",
                        FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                        Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*",
                        DefaultExt = ".png",
                    };

                    if (dialog.ShowDialog() == true && vm.Model.ImagePath != null)
                    {
                        var bitmap = _images.LoadFull(vm.Model.ImagePath);
                        if (bitmap != null)
                        {
                            if (dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                            {
                                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                                using var fs = File.Create(dialog.FileName);
                                encoder.Save(fs);
                            }
                            else
                            {
                                File.Copy(vm.Model.ImagePath, dialog.FileName, overwrite: true);
                            }

                            ShowToast("已保存图片");
                        }
                    }

                    break;
                }

                case ClipboardItemType.Files:
                {
                    OpenInFolder(vm);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("另存为失败", ex);
            ShowToast("保存失败：" + ex.Message);
        }
        finally
        {
            ModalStateChanged?.Invoke(this, false);
        }
    }

    [RelayCommand]
    private void OpenInFolder(ClipboardItemViewModel? vm)
    {
        if (vm == null)
        {
            return;
        }

        try
        {
            string? target = vm.Model.Type switch
            {
                ClipboardItemType.Image => vm.Model.ImagePath,
                ClipboardItemType.Files => vm.Model.FilePaths.FirstOrDefault(),
                _ => null,
            };

            if (string.IsNullOrEmpty(target))
            {
                ShowToast("文本记录没有对应的文件");
                return;
            }

            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("打开所在文件夹失败", ex);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开数据目录失败", ex);
        }
    }

    /// <summary>页面切换（Emoji / 设置；托盘菜单等程序化调用也走这里）。</summary>
    [RelayCommand]
    private void SetPage(string? page)
    {
        Page = page switch
        {
            "emoji" => MainPage.Emoji,
            "settings" => MainPage.Settings,
            _ => MainPage.History,
        };
    }

    /// <summary>
    /// 顶部的“全部 / 文本 / 图片 / 文件 / ★”胶囊。
    /// 它同时承担“回到历史页”的语义：从 Emoji / 设置页点任意筛选都会切回历史列表，
    /// 这样顶部就不会出现“点了胶囊但界面没反应”的情况（“全部”即完整历史列表，
    /// 不再需要单独的“历史”按钮）。
    /// </summary>
    [RelayCommand]
    private void SetFilter(string? filter)
    {
        Filter = filter switch
        {
            "text" => HistoryFilter.Text,
            "image" => HistoryFilter.Image,
            "files" => HistoryFilter.Files,
            "pinned" => HistoryFilter.Pinned,
            _ => HistoryFilter.All,
        };

        Page = MainPage.History;
    }

    [RelayCommand]
    private void TogglePause() => MonitorPaused = !MonitorPaused;

    [RelayCommand]
    private void ToggleAutoPaste() => AutoPaste = !AutoPaste;

    [RelayCommand]
    private void PickEmoji(EmojiEntry? entry)
    {
        if (entry != null)
        {
            Emoji.Pick(entry);
        }
    }

    private void OnEmojiPicked(object? sender, EmojiEntry entry)
    {
        try
        {
            if (!_writer.SetText(entry.Value))
            {
                ShowToast("复制失败：剪切板被占用");
                return;
            }

            PasteRequested?.Invoke(this, new PasteRequestEventArgs(null, AutoPaste, "Emoji " + entry.Value));
            if (!AutoPaste)
            {
                ShowToast($"已复制 {entry.Value} {entry.Name}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("复制 Emoji 失败", ex);
        }
    }

    // ------------------------------------------------------------------
    // 提示条
    // ------------------------------------------------------------------

    public void ShowToast(string message)
    {
        ToastMessage = message;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>面板显示时刷新（脏数据重载 + 状态文案）。</summary>
    public void OnPanelShown()
    {
        StatusText = MonitoringStateText;
        OnPropertyChanged(nameof(RunAtStartup));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(DeferredHideDelayMs));
        OnPropertyChanged(nameof(DeferredHideDelayInput));
        RefreshWinVHookState();
    }

    /// <summary>钩子挂载/卸载后刷新 Win+V 相关文案（由 AppHost 在启动或停止钩子后调用）。</summary>
    public void RefreshWinVHookState()
    {
        OnPropertyChanged(nameof(WinVHookEnabled));
        OnPropertyChanged(nameof(WinVHookStatusText));
        OnPropertyChanged(nameof(MonitoringHint));
    }

    /// <summary>请求保存设置（去抖，避免频繁写盘）。</summary>
    public void RequestSettingsSave()
    {
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
