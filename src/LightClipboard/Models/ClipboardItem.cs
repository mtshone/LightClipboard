using System;
using System.Collections.Generic;

namespace LightClipboard.Models;

/// <summary>
/// 一条剪切板历史记录（LiteDB 文档模型，Id 映射为 _id）。
/// 该类型只作为存储/传输用的纯数据对象，展示相关的计算属性放在 ViewModel 中。
/// </summary>
public sealed class ClipboardItem
{
    /// <summary>LiteDB 自增主键。</summary>
    public int Id { get; set; }

    /// <summary>条目类型。</summary>
    public ClipboardItemType Type { get; set; }

    /// <summary>内容指纹（SHA-256，十六进制），用于去重。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>文本内容（仅 <see cref="ClipboardItemType.Text"/>）。</summary>
    public string? Text { get; set; }

    /// <summary>图片文件的绝对路径（仅 <see cref="ClipboardItemType.Image"/>，指向本地缓存目录）。</summary>
    public string? ImagePath { get; set; }

    /// <summary>图片像素宽度。</summary>
    public int ImageWidth { get; set; }

    /// <summary>图片像素高度。</summary>
    public int ImageHeight { get; set; }

    /// <summary>内容字节数（文本为 UTF-8 长度，图片为 PNG 文件长度）。</summary>
    public long ByteSize { get; set; }

    /// <summary>文件路径集合（仅 <see cref="ClipboardItemType.Files"/>）。</summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>来源进程名（尽力而为，可能为空）。</summary>
    public string SourceApp { get; set; } = string.Empty;

    /// <summary>首次进入历史的时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 排序键（列表按它倒序）：最近一次**进入**剪切板的时间 —— 入库或去重命中时刷新。
    /// 面板输出（点击卡片复制 / 粘贴 / 拖出）不改写它，所以列表顺序是稳定的历史顺序。
    /// 字段名保持不变：LiteDB 按属性名映射，改名会让旧记录静默丢掉这个字段。
    /// </summary>
    public DateTime LastUsedAt { get; set; } = DateTime.Now;

    /// <summary>是否已收藏（收藏项不会被自动清理，并始终排在最前）。</summary>
    public bool IsPinned { get; set; }

    /// <summary>创建一个浅拷贝。</summary>
    public ClipboardItem Clone() => new()
    {
        Id = Id,
        Type = Type,
        Hash = Hash,
        Text = Text,
        ImagePath = ImagePath,
        ImageWidth = ImageWidth,
        ImageHeight = ImageHeight,
        ByteSize = ByteSize,
        FilePaths = new List<string>(FilePaths),
        SourceApp = SourceApp,
        CreatedAt = CreatedAt,
        LastUsedAt = LastUsedAt,
        IsPinned = IsPinned,
    };
}
