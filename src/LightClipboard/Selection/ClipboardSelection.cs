using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using LightClipboard.Models;
using LightClipboard.ViewModels;

namespace LightClipboard.Selection;

/// <summary>
/// 多选（本期仅图片）选择状态机。
///
/// 刻意与 <c>MainViewModel</c> 解耦：选择是"面板内的一次交互状态"，不是持久化数据，
/// 放进独立类型后可被 <c>--selftest</c> 直接构造断言（MainViewModel 已超过 1000 行）。
///
/// 三条约定：
/// - **唯一写入方**：只有本类型能改 <see cref="ClipboardItemViewModel.IsMultiSelected"/>；
/// - **输出顺序 = 队列顺序**（<see cref="_selected"/> 本身就是拖出队列，不再做任何列表序重排）：
///   有 Ctrl 参与（<see cref="_ctrlInvolved"/>）时 = **点击顺序** —— 每次 Ctrl 新增都追加到队列末尾，
///   之后再按 Shift 只把"区间里还没进队列的图片"按列表顺序补到末尾（扩展，不重排、不取消已选项）；
///   纯 Shift（全程没有 Ctrl 参与）时 = **列表顺序** —— 把"锚点到目标"的区间按 index 升序整体换入队列。
///   取消后重新 Ctrl 选中会排到队列末尾（不会回到它原来的位置）。
/// - **只在 Reset 时清空**：整体重建（切筛选 / 搜索 / 重新装载）清空选择，
///   增量变更（新内容入库的 Insert / Move）不打断已经选好的集合。
/// </summary>
public sealed class ClipboardSelection
{
    private readonly ObservableCollection<ClipboardItemViewModel> _items;

    /// <summary>
    /// 输出队列：<see cref="BuildDragItems"/> 直接按这个顺序拖出。
    /// 新增项一律追加到末尾（点击顺序），移除按引用删除，绝不整体重排。
    /// </summary>
    private readonly List<ClipboardItemViewModel> _selected = new();

    /// <summary>Shift 范围选择的起点（最近一次普通单击 / Ctrl 点击的图片）。</summary>
    private ClipboardItemViewModel? _anchor;

    /// <summary>
    /// 当前队列顺序里是否有 Ctrl 参与（true ⇒ 点击顺序）。有 Ctrl 参与时后续 Shift 只在末尾扩展，
    /// 不会把队列重排/替换回列表顺序；纯 Shift 时区间整体替换选择。
    /// </summary>
    private bool _ctrlInvolved;

    public ClipboardSelection(ObservableCollection<ClipboardItemViewModel> items)
    {
        _items = items;

        // 列表整体重建（RebuildView / Load / 切筛选 / 搜索）时自动清空：
        // 否则会出现"选中的项已经不可见，但页脚还显示已选 3 张"的幽灵状态。
        // 只处理 Reset —— Insert / Move（新内容入库）不该打断用户已经选好的集合。
        _items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>选择发生变化（增删或清空）。</summary>
    public event EventHandler? Changed;

    /// <summary>当前选中的卡片，按拖出队列顺序（有 Ctrl 参与时 = 点击顺序，纯 Shift 时 = 列表顺序）。</summary>
    public IReadOnlyList<ClipboardItemViewModel> Selected => _selected;

    public int Count => _selected.Count;

    public bool HasSelection => _selected.Count > 0;

    /// <summary>页脚提示文案；无选择时为空串（调用方据此隐藏那一行）。</summary>
    public string SelectionText => _selected.Count == 0
        ? string.Empty
        : $"已选 {_selected.Count} 张图片 · 拖动其中任意一张可全部拖出";

    public bool IsSelected(ClipboardItemViewModel vm) => _selected.Contains(vm);

    /// <summary>普通单击：清空多选，并把这张图片设为新的锚点。</summary>
    public void SelectSingleImage(ClipboardItemViewModel vm)
    {
        bool had = _selected.Count > 0;
        ClearCore();

        if (vm.Type == ClipboardItemType.Image)
        {
            _anchor = vm;
        }

        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Ctrl+单击：切换单张。返回 false 表示这次点击被拒绝（非图片），调用方给一条 Toast。
    /// 拒绝时**不改变**已有选择（比"清空"更安全）。
    ///
    /// 新增的图片追加到队列**末尾**（点击顺序），不做列表序重排；
    /// 两条路径都把"有 Ctrl 参与"记下来，之后整批拖出的顺序就是点击顺序。
    /// </summary>
    public bool ToggleImage(ClipboardItemViewModel vm)
    {
        if (vm.Type != ClipboardItemType.Image)
        {
            return false;
        }

        _ctrlInvolved = true;

        bool removed = _selected.Remove(vm);
        if (removed)
        {
            vm.SetMultiSelected(false);
        }
        else
        {
            _selected.Add(vm);
            vm.SetMultiSelected(true);
        }

        if (removed)
        {
            // 被取消的可能正好是锚点：退到队列末尾那张，不留悬空锚点
            if (ReferenceEquals(_anchor, vm))
            {
                _anchor = _selected.Count > 0 ? _selected[^1] : null;
            }
        }
        else
        {
            _anchor = vm;   // 最近一次 Ctrl 选中的那张成为新锚点
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Shift+单击：从锚点到目标卡片在**可视列表**里的连续区间，只把其中的图片纳入选择。
    /// 区间里的文本 / 文件被跳过（它们不参与导出，见需求边界）。
    ///
    /// 调用方只在"真的有新点击"时调用（MouseUp 分派），因此这里用"区间序列 vs 现状"的比较决定动作：
    /// - **纯 Shift**（没有 Ctrl 参与）：区间序列（按 index 升序）就是新的全集，整体换入 —— 输出按列表顺序；
    /// - **Ctrl 参与过**：队列 = 点击顺序，区间只在**末尾追加**尚未进队列的图片（按列表顺序），
    ///   已在队列里的项保持原位（也不取消）—— 即 Ctrl+Shift 式的"扩展"，点击序不被重置；
    /// - 集合与顺序与现状完全一致时什么都不做（不重置点击序，也不做无谓的视觉 / 事件抖动）。
    ///
    /// 锚点必须按 <c>Items</c> 的 index 计算：列表开了虚拟化，
    /// 没实例化的卡片不在可视化树里，按可视化树算范围必然错。
    /// </summary>
    public void SelectRangeImages(ClipboardItemViewModel vm)
    {
        // 锚点为空（或已不在当前列表里）时退化为"只选这一张"（清空多选 + 该张成为新锚点）
        if (_anchor == null || !_items.Contains(_anchor))
        {
            SelectSingleImage(vm);
            return;
        }

        int anchorIndex = _items.IndexOf(_anchor);
        int targetIndex = _items.IndexOf(vm);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            SelectSingleImage(vm);
            return;
        }

        int from = Math.Min(anchorIndex, targetIndex);
        int to = Math.Max(anchorIndex, targetIndex);

        // 区间序列：按 _items 的 index 升序，跳过文本 / 文件 —— 这便是"纯 Shift"时的输出顺序。
        // 锚点本身必是图片且在列表里，所以区间里至少含锚点这一张。
        var range = new List<ClipboardItemViewModel>();
        for (int i = from; i <= to; i++)
        {
            var candidate = _items[i];
            if (candidate.Type == ClipboardItemType.Image)
            {
                range.Add(candidate);
            }
        }

        // ClearCore 会把锚点一起清掉，所以先留住它：
        // 连续多次 Shift 点击要以同一个锚点为基准反复调整范围（方案 §2.2 / 边界 4）。
        var anchor = _anchor;

        if (_ctrlInvolved)
        {
            // Ctrl 参与过 ⇒ 保持点击顺序：只把区间里"还没进队列"的图片按列表顺序补到末尾。
            // 逐项 Contains 判重（沿用旧排序兜底 wanted.Remove 的语义：只有"还没进队列"的才追加）：
            // 同一项绝不可能被追加两次 —— 否则会出现"选了 3 张、拖出去落了 5 个文件"。
            var appended = range.Where(candidate => !_selected.Contains(candidate)).ToList();
            if (appended.Count > 0)
            {
                foreach (var candidate in appended)
                {
                    _selected.Add(candidate);
                    candidate.SetMultiSelected(true);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }

            _anchor = anchor;   // 关键：锚点不动
            return;
        }

        if (range.SequenceEqual(_selected))
        {
            // 集合与顺序都没变（连续 Shift 到同一区间）→ 保持 _selected 原样、_ctrlInvolved 不变。
            _anchor = anchor;
            return;
        }

        // 纯 Shift：区间整体换入选择（列表顺序）。ClearCore 顺带复位 _ctrlInvolved。
        ClearCore();
        foreach (var candidate in range)
        {
            _selected.Add(candidate);
            candidate.SetMultiSelected(true);
        }

        _anchor = anchor;   // 关键：锚点不动
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空选择（面板收起 / 列表重建 / 普通单击时调用）。</summary>
    public void Clear()
    {
        // 队列已空且没有 Ctrl 参与痕迹 → 真正的无事可做（不打扰界面）
        if (_selected.Count == 0 && !_ctrlInvolved)
        {
            return;
        }

        bool had = _selected.Count > 0;
        ClearCore();

        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 取当前选中的图片，按**拖出队列顺序**返回，供拖拽导出使用。
    /// 这里刻意不做任何重排：队列顺序就是点击顺序（纯 Shift 时它本身就是按列表 index 升序建的）。
    /// </summary>
    public IReadOnlyList<ClipboardItem> BuildDragItems()
    {
        return _selected
            .Select(vm => vm.Model)
            .Where(m => m.Type == ClipboardItemType.Image && !string.IsNullOrWhiteSpace(m.ImagePath))
            .ToList();
    }

    private void ClearCore()
    {
        foreach (var vm in _selected)
        {
            vm.SetMultiSelected(false);
        }

        _selected.Clear();
        _anchor = null;
        _ctrlInvolved = false;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Clear();
        }
    }
}
