using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using LightClipboard.Models;
using LightClipboard.Services;

namespace LightClipboard.ViewModels;

/// <summary>
/// 单条剪切板卡片的视图模型。所有展示文本都在这里预先算好，
/// 避免 XAML 中堆积转换逻辑，也方便对 Emoji（代理对）做安全截断。
/// </summary>
public sealed class ClipboardItemViewModel : ObservableObject
{
    private const int PreviewCharLimit = 480;

    private ClipboardItem _model;

    /// <summary>多选态（由 ClipboardSelection 维护，见 <see cref="SetMultiSelected"/>）。</summary>
    private bool _isMultiSelected;

    public ClipboardItemViewModel(ClipboardItem model)
    {
        _model = model;
    }

    public ClipboardItem Model => _model;

    public int Id => _model.Id;

    public ClipboardItemType Type => _model.Type;

    public bool IsPinned => _model.IsPinned;

    public bool IsText => _model.Type == ClipboardItemType.Text;

    public bool IsImage => _model.Type == ClipboardItemType.Image;

    public bool IsFiles => _model.Type == ClipboardItemType.Files;

    public DateTime LastUsedAt => _model.LastUsedAt;

    /// <summary>用于缩略图展示的图片路径：图片本身，或“仅含一张图片”的文件条目。</summary>
    public string? PreviewImagePath
    {
        get
        {
            if (_model.Type == ClipboardItemType.Image)
            {
                return _model.ImagePath;
            }

            if (_model.Type == ClipboardItemType.Files
                && _model.FilePaths.Count == 1
                && ClipboardDataParser.IsImageFile(_model.FilePaths[0])
                && File.Exists(_model.FilePaths[0]))
            {
                return _model.FilePaths[0];
            }

            return null;
        }
    }

    public bool HasPreviewImage => PreviewImagePath != null;

    /// <summary>卡片主标题（文件条目显示文件名，其余显示内容预览）。</summary>
    public string Title => _model.Type switch
    {
        ClipboardItemType.Text => TextPreview,
        ClipboardItemType.Image => $"图片 {_model.ImageWidth} × {_model.ImageHeight}",
        ClipboardItemType.Files => FileTitle,
        _ => string.Empty,
    };

    /// <summary>文本预览（安全截断，不会截断 Emoji 代理对）。</summary>
    public string TextPreview
    {
        get
        {
            string text = _model.Text ?? string.Empty;
            string normalized = Normalize(text);
            return TruncateTextElements(normalized, PreviewCharLimit);
        }
    }

    public string FileTitle
    {
        get
        {
            if (_model.FilePaths.Count == 0)
            {
                return "空文件列表";
            }

            if (_model.FilePaths.Count == 1)
            {
                return GetDisplayName(_model.FilePaths[0]);
            }

            return $"{GetDisplayName(_model.FilePaths[0])} 等 {_model.FilePaths.Count} 个文件";
        }
    }

    /// <summary>副标题：来源应用 + 时间 + 体量。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(_model.SourceApp))
            {
                parts.Add(_model.SourceApp);
            }

            parts.Add(TimeText);
            parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>相对时间描述。</summary>
    public string TimeText
    {
        get
        {
            var span = DateTime.Now - _model.LastUsedAt;
            if (span.TotalSeconds < 45)
            {
                return "刚刚";
            }

            if (span.TotalMinutes < 60)
            {
                return $"{(int)span.TotalMinutes} 分钟前";
            }

            if (_model.LastUsedAt.Date == DateTime.Today)
            {
                return _model.LastUsedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            if (_model.LastUsedAt.Date == DateTime.Today.AddDays(-1))
            {
                return "昨天 " + _model.LastUsedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            return _model.LastUsedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>体量描述。</summary>
    public string SizeText => _model.Type switch
    {
        ClipboardItemType.Text => $"{(_model.Text?.Length ?? 0):N0} 字符",
        ClipboardItemType.Image => FormatBytes(_model.ByteSize),
        ClipboardItemType.Files => $"{_model.FilePaths.Count} 项",
        _ => string.Empty,
    };

    /// <summary>类型图标（Segoe Fluent Icons 字形）。</summary>
    public string TypeGlyph => _model.Type switch
    {
        ClipboardItemType.Text => "\uE8A5",
        ClipboardItemType.Image => "\uEB9F",
        ClipboardItemType.Files => "\uE8B7",
        _ => "\uE8A5",
    };

    public string TypeName => _model.Type switch
    {
        ClipboardItemType.Text => "文本",
        ClipboardItemType.Image => "图片",
        ClipboardItemType.Files => "文件",
        _ => "内容",
    };

    public string PinGlyph => _model.IsPinned ? "\uE77A" : "\uE718";

    public string PinTooltip => _model.IsPinned ? "取消收藏" : "收藏（不会被自动清理）";

    /// <summary>
    /// 是否在多选集合里（仅图片会为 true）。与 ListBox 自身的 IsSelected（"当前项"）是两件事：
    /// 前者表示"会被一起拖出去的批次"，后者只是键盘光标所在的那张卡。
    /// </summary>
    public bool IsMultiSelected => _isMultiSelected;

    /// <summary>
    /// 由 <see cref="LightClipboard.Selection.ClipboardSelection"/> 维护。
    /// **刻意不公开 setter**：选择状态的唯一写入方必须是选择状态机，
    /// 否则会出现"XAML 绑定也能改选择"的两套真相。
    /// </summary>
    public void SetMultiSelected(bool value)
    {
        if (_isMultiSelected == value)
        {
            return;
        }

        _isMultiSelected = value;
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    /// <summary>搜索匹配。</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        if (_model.Type == ClipboardItemType.Text
            && _model.Text != null
            && _model.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_model.Type == ClipboardItemType.Files
            && _model.FilePaths.Any(p => p.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return TypeName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || _model.SourceApp.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用新的数据刷新（去重命中时更新文本与时间）。</summary>
    public void Update(ClipboardItem model)
    {
        _model = model;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>按 Emoji 安全的方式截断字符串（不会把代理对从中间切开）。</summary>
    public static string TruncateTextElements(string text, int maxTextElements)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        int end = 0;
        while (enumerator.MoveNext())
        {
            count++;
            end = enumerator.ElementIndex + ((string)enumerator.Current!).Length;
            if (count >= maxTextElements)
            {
                return end >= text.Length ? text : text[..end] + "…";
            }
        }

        return text;
    }

    /// <summary>压缩空白，保持可读的多行预览。</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lastWasNewline = false;
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                if (!lastWasNewline)
                {
                    builder.Append('\n');
                }

                lastWasNewline = true;
                lastWasSpace = false;
                continue;
            }

            if (c is ' ' or '\t')
            {
                if (!lastWasSpace && !lastWasNewline)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasNewline = false;
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    public static string GetDisplayName(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return path;
        }
        catch
        {
            return path;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}
