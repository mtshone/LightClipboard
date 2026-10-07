using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightClipboard.Interop;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>一次剪切板读取的结果。</summary>
public sealed class ParsedClipboardContent
{
    public ClipboardItemType Type { get; init; }

    public string? Text { get; init; }

    public BitmapSource? Image { get; init; }

    public List<string> FilePaths { get; init; } = new();

    /// <summary>来源图片是否是带真实透明通道的 PNG（来自浏览器等）。</summary>
    public bool PreserveAlpha { get; init; }

    public string SourceApp { get; init; } = string.Empty;
}

/// <summary>
/// 剪切板数据解析：识别 Text/UnicodeText、Bitmap/DIB/PNG、FileDrop 等格式，
/// 并针对 CLIPBRD_E_CANT_OPEN（剪切板被其他程序独占）做退避重试（50/100/150ms 等差递增）。
/// </summary>
public sealed class ClipboardDataParser
{
    private const int MaxAttempts = 3;
    private const string PngFormat = "PNG";

    private static readonly string[] ImageFileExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

    private readonly AppSettings _settings;

    public ClipboardDataParser(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>判断路径是否为常见图片文件。</summary>
    public static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ImageFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取当前剪切板内容。返回 false 表示剪切板为空或读取失败（不抛异常）。
    /// 必须在 UI（STA）线程调用。
    /// </summary>
    public bool TryRead(out ParsedClipboardContent? content)
    {
        content = null;
        Exception? lastError = null;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                content = ReadCore();
                return content != null;
            }
            catch (COMException ex)
            {
                // 剪切板可能被其他程序独占锁住（CLIPBRD_E_CANT_OPEN），退避后重试
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

            System.Threading.Thread.Sleep(50 * (attempt + 1));
        }

        if (lastError != null)
        {
            Log.Warn("读取剪切板失败: " + lastError.Message, lastError);
        }

        return false;
    }

    private ParsedClipboardContent? ReadCore()
    {
        IDataObject? data = Clipboard.GetDataObject();
        if (data == null)
        {
            return null;
        }

        string sourceApp = TryGetForegroundProcessName();

        // 1) 文件列表（资源管理器复制的文件/文件夹）
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var existing = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
                if (existing.Count > 0)
                {
                    return new ParsedClipboardContent
                    {
                        Type = ClipboardItemType.Files,
                        FilePaths = existing,
                        SourceApp = sourceApp,
                    };
                }
            }
        }

        // 2) 图片（含 Win+Shift+S 截图）
        var image = TryReadImage(data, out bool preserveAlpha);
        if (image != null)
        {
            return new ParsedClipboardContent
            {
                Type = ClipboardItemType.Image,
                Image = image,
                PreserveAlpha = preserveAlpha,
                SourceApp = sourceApp,
            };
        }

        // 3) 文本 / Emoji（.NET 字符串本身就是 UTF-16，天然支持 Unicode 与 Emoji）
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            string? text = null;
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                text = data.GetData(DataFormats.UnicodeText) as string;
            }

            text ??= data.GetData(DataFormats.Text) as string;

            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > _settings.MaxTextLength)
                {
                    text = text[.._settings.MaxTextLength];
                }

                return new ParsedClipboardContent
                {
                    Type = ClipboardItemType.Text,
                    Text = text,
                    SourceApp = sourceApp,
                };
            }
        }

        return null;
    }

    private static BitmapSource? TryReadImage(IDataObject data, out bool preserveAlpha)
    {
        preserveAlpha = false;

        // 优先使用 PNG 流：画质最好且保留透明通道
        try
        {
            if (data.GetDataPresent(PngFormat) && data.GetData(PngFormat) is Stream pngStream)
            {
                using (pngStream)
                {
                    pngStream.Position = 0;
                    var frame = BitmapFrame.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    preserveAlpha = true;
                    return frame;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪切板 PNG 格式失败，回退到 DIB", ex);
        }

        // 回退到 CF_BITMAP / CF_DIB
        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))
            {
                BitmapSource? source = Clipboard.GetImage();
                if (source != null)
                {
                    // CF_DIB 的 alpha 通道通常是无效数据（全 0），
                    // 直接保存会出现“截图全透明”的经典问题，因此这里强制不透明。
                    return MakeOpaque(source);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪切板图片失败", ex);
        }

        return null;
    }

    /// <summary>把位图转换为 Bgra32 并把 alpha 全部置为 255（不透明）。</summary>
    public static BitmapSource MakeOpaque(BitmapSource source)
        => ImageCacheManager.Normalize(source, preserveAlpha: false);

    /// <summary>取当前前台窗口的进程名，作为“来源应用”展示。</summary>
    public static string TryGetForegroundProcessName()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
