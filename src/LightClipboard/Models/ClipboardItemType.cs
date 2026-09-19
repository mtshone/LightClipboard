using System;

namespace LightClipboard.Models;

/// <summary>
/// 剪切板条目类型。
/// </summary>
public enum ClipboardItemType
{
    /// <summary>纯文本（包含 Emoji / Unicode 文本）。</summary>
    Text = 0,

    /// <summary>位图图片（含 Win+Shift+S 截图、浏览器/Office 复制的图片）。</summary>
    Image = 1,

    /// <summary>文件列表（资源管理器中复制的文件/文件夹）。</summary>
    Files = 2,
}
