using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>
/// 剪切板写入（把历史条目重新复制回系统剪切板）。
/// 通过 <see cref="IsSuppressed"/> 屏蔽自身写入导致的 WM_CLIPBOARDUPDATE，
/// 避免“复制 → 监听 → 再复制”的死循环。
/// </summary>
public sealed class ClipboardWriter
{
    private const int MaxAttempts = 5;

    private readonly ImageCacheManager _images;
    private DateTime _suppressUntil = DateTime.MinValue;

    public ClipboardWriter(ImageCacheManager images)
    {
        _images = images;
    }

    /// <summary>当前是否处于“自写入屏蔽窗口”内。</summary>
    public bool IsSuppressed => DateTime.UtcNow < _suppressUntil;

    /// <summary>开启一段屏蔽窗口。</summary>
    public void Suppress(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until > _suppressUntil)
        {
            _suppressUntil = until;
        }
    }

    /// <summary>写入纯文本（支持 Emoji / Unicode）。</summary>
    public bool SetText(string text)
    {
        return WriteWithRetry(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }, "写入文本");
    }

    /// <summary>写入文件列表。</summary>
    public bool SetFiles(IEnumerable<string> paths)
    {
        var collection = new System.Collections.Specialized.StringCollection();
        foreach (string path in paths)
        {
            collection.Add(path);
        }

        if (collection.Count == 0)
        {
            return false;
        }

        return WriteWithRetry(() =>
        {
            var data = new DataObject();
            data.SetFileDropList(collection);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }, "写入文件列表");
    }

    /// <summary>写入图片（Bitmap + PNG 流两种格式，兼容 Office / 画图 / 聊天软件）。</summary>
    public bool SetImage(BitmapSource bitmap, string? imagePath, bool includeFilePath)
    {
        byte[]? png = null;
        try
        {
            png = ImageCacheManager.EncodePng(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn("编码 PNG 失败，仅写入位图格式", ex);
        }

        return WriteWithRetry(() =>
        {
            var data = new DataObject();
            data.SetImage(bitmap);

            if (png != null)
            {
                var stream = new MemoryStream(png);
                data.SetData("PNG", stream);
            }

            if (includeFilePath && !string.IsNullOrWhiteSpace(imagePath))
            {
                string? export = _images.EnsureExportFile(imagePath);
                if (export != null)
                {
                    var collection = new System.Collections.Specialized.StringCollection { export };
                    data.SetFileDropList(collection);
                }
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }, "写入图片");
    }

    /// <summary>把一条历史记录写回剪切板。</summary>
    public bool SetItem(ClipboardItem item, bool includeImageFilePath)
    {
        switch (item.Type)
        {
            case ClipboardItemType.Text:
                return item.Text is { Length: > 0 } text && SetText(text);

            case ClipboardItemType.Image:
            {
                var bitmap = _images.LoadFull(item.ImagePath);
                return bitmap != null && SetImage(bitmap, item.ImagePath, includeImageFilePath);
            }

            case ClipboardItemType.Files:
                return SetFiles(item.FilePaths);

            default:
                return false;
        }
    }

    private bool WriteWithRetry(Func<bool> action, string description)
    {
        // 写入前先开启屏蔽窗口：SetDataObject 之后系统立刻就会广播 WM_CLIPBOARDUPDATE
        Suppress(TimeSpan.FromSeconds(1.5));

        Exception? lastError = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (action())
                {
                    return true;
                }
            }
            catch (COMException ex)
            {
                lastError = ex;
            }
            catch (ExternalException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }

            System.Threading.Thread.Sleep(60 * (attempt + 1));
        }

        Log.Warn($"{description}失败（剪切板可能被占用）", lastError);
        return false;
    }

    /// <summary>计算文本内容指纹。</summary>
    public static string ComputeTextHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>计算文件列表内容指纹。</summary>
    public static string ComputeFilesHash(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        foreach (string path in paths)
        {
            sb.Append(path.ToLower(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
