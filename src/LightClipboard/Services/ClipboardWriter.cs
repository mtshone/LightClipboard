using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightClipboard.Interop;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>
/// 剪切板写入（把历史条目重新复制回系统剪切板）。
/// 通过 <see cref="IsSuppressed"/> 屏蔽自身写入导致的 WM_CLIPBOARDUPDATE，
/// 避免“复制 → 监听 → 再复制”的死循环。
///
/// 另负责「输出 = 借一次就还」：<see cref="CaptureForRestore"/> 在写之前存下剪切板原样，
/// 粘贴送达后由 <see cref="ScheduleRestoreAfterPaste"/> → <see cref="RestoreNow"/> 还回去，
/// 于是用户随后按 Ctrl+V 得到的仍是输出前那份内容（正常情形就是列表顶部那条）。
/// </summary>
public sealed class ClipboardWriter
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// 粘贴按键发出后等多久再还原剪切板（ms）。
    /// 不能立即还原：目标程序是在自己的消息循环里响应 WM_PASTE 的，抢在它读取之前写回，
    /// 粘贴会拿到旧内容甚至失败。300ms 对记事本 / 浏览器 / 聊天软件都足够（自检与真机验收都验过）。
    /// </summary>
    private const int RestoreDelayMs = 300;

    private readonly ImageCacheManager _images;
    private readonly DispatcherTimer _restoreTimer;
    private DateTime _suppressUntil = DateTime.MinValue;

    /// <summary>输出前的剪切板快照（null = 没有待还原的内容）。</summary>
    private List<SnapshotEntry>? _backup;

    public ClipboardWriter(ImageCacheManager images)
    {
        _images = images;

        // 计时器必须在 UI（STA）线程创建与触发：剪切板 API 不允许跨线程调用
        _restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RestoreDelayMs) };
        _restoreTimer.Tick += (_, _) =>
        {
            _restoreTimer.Stop();
            RestoreNow();
        };
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

    // ------------------------------------------------------------------
    // 输出 = 借一次就还：输出前的快照与粘贴送达后的还原
    // ------------------------------------------------------------------

    /// <summary>
    /// WPF 按"纯文本"正确处理的格式：写回时交给 WPF 的字符串通道，编码/字节序由它负责。
    /// **HTML Format / Rich Text Format 刻意不在这里** —— 它们的载荷是 UTF-8/ANSI 字节，
    /// WPF 虽然也当字符串读，但走字符串通道会被改写（真机实测 RTF 会被整段吃成空串，见 CHANGELOG v1.2.4），
    /// 这两类格式改走下面的"原始 HGLOBAL 字节"通道。
    /// </summary>
    private static readonly HashSet<string> PlainTextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.OemText,
    };

    /// <summary>是否已存下一份待还原的剪切板快照。</summary>
    public bool HasRestorePending => _backup is { Count: > 0 };

    /// <summary>
    /// 在把历史条目写进剪切板（"借"）之前，把当前剪切板整份存下来。
    ///
    /// 只有**真的会在目标程序里粘贴**的输出才需要它：粘贴送达后调 <see cref="ScheduleRestoreAfterPaste"/> 还回去，
    /// 这样用户随后按 Ctrl+V 拿到的仍是输出前那份内容（正常情形就是列表顶部那条），而不是刚被输出的这条 ——
    /// 系统剪切板的"当前内容"因此不再被"用过一次"的历史条目顶掉，与"列表顺序不因输出而变"配套。
    ///
    /// "只复制不粘贴"的路径**不要**调它：那条路径的语义正是"把这一条留在剪切板里等用户自己 Ctrl+V"。
    /// </summary>
    /// <returns>是否抓到了可还原的内容（剪切板为空、或所有格式都抓不动时为 false，此后不会还原）。</returns>
    public bool CaptureForRestore()
    {
        DiscardRestore();

        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null)
            {
                Log.Info("输出前剪切板为空，不建立快照（这次输出不会被还原）");
                return false;
            }

            var entries = new List<SnapshotEntry>();
            foreach (string format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var entry = CaptureFormat(source, format);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"剪切板快照跳过格式 {format}", ex);
                }
            }

            if (entries.Count == 0)
            {
                Log.Info("输出前剪切板没有可还原的格式，这次输出不会被还原");
                return false;
            }

            _backup = entries;
            Log.Info($"已存下输出前的剪切板快照：{string.Join("、", entries.Select(Describe))}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("抓输出前的剪切板快照失败（这次输出不会被还原）", ex);
            _backup = null;
            return false;
        }
    }

    /// <summary>丢弃快照：写入失败、或这次输出最终没有真的粘贴（目标窗口无效、按键没发出去）时调用。</summary>
    public void DiscardRestore() => _backup = null;

    /// <summary>粘贴按键已送达：延迟一小段时间后把剪切板还原成输出前的内容。</summary>
    public void ScheduleRestoreAfterPaste()
    {
        if (!HasRestorePending)
        {
            return;
        }

        _restoreTimer.Stop();
        _restoreTimer.Start();
        Log.Info($"粘贴已送达，将在 {RestoreDelayMs} ms 后把剪切板还原为输出前的内容");
    }

    /// <summary>立即把剪切板还原成快照内容（计时器与自检共用；返回是否真的写回了一次）。</summary>
    public bool RestoreNow()
    {
        _restoreTimer.Stop();

        var entries = _backup;
        _backup = null;
        if (entries is not { Count: > 0 })
        {
            return false;
        }

        bool restored = WriteWithRetry(
            () =>
            {
                var data = new DataObject();
                foreach (var entry in entries)
                {
                    switch (entry.Payload)
                    {
                        case SnapshotPayload.Bitmap:
                            data.SetImage((BitmapSource)entry.Value);
                            break;

                        case SnapshotPayload.Files:
                            var collection = new StringCollection();
                            collection.AddRange((string[])entry.Value);
                            data.SetFileDropList(collection);
                            break;

                        case SnapshotPayload.RawBytes:
                            // 原始 HGLOBAL 字节按流写回：探针实测 HTML / RTF / PNG 都逐字节一致
                            data.SetData(entry.Format, new MemoryStream((byte[])entry.Value));
                            break;

                        default:
                            data.SetData(entry.Format, (string)entry.Value);
                            break;
                    }
                }

                Clipboard.SetDataObject(data, copy: true);
                return true;
            },
            "还原剪切板");

        if (restored)
        {
            Log.Info($"剪切板已还原为输出前的内容（{entries.Count} 种格式），按 Ctrl+V 得到的仍是列表顶部那条");
        }
        else
        {
            Log.Warn("剪切板还原失败：剪切板里保留的是刚输出的那条");
        }

        return restored;
    }

    /// <summary>抓单个格式；返回 null 表示这个格式抓不动（跳过它不影响其它格式）。</summary>
    private static SnapshotEntry? CaptureFormat(IDataObject source, string format)
    {
        object? data = source.GetData(format, autoConvert: false);

        // 位图必须走 WPF 的位图通道：写回时 SetImage 会生成正确的 DIB / BITMAP 载荷
        if (data is BitmapSource bitmap)
        {
            return new SnapshotEntry(format, SnapshotPayload.Bitmap, CopyBitmap(bitmap));
        }

        // 文件列表：FileDrop 走 SetFileDropList；
        // FileName / FileNameW 这类老格式返回的也是 string[]，交给下面的原始字节通道
        if (format == DataFormats.FileDrop)
        {
            if (data is string[] files)
            {
                return new SnapshotEntry(format, SnapshotPayload.Files, files);
            }

            if (data is StringCollection collection)
            {
                return new SnapshotEntry(format, SnapshotPayload.Files, collection.Cast<string>().ToArray());
            }
        }

        // 流（PNG / Locale 等）：复制成独立的字节缓冲
        // （RawBytes 一律存 byte[]，别存 MemoryStream —— 还原侧按 byte[] 写回，混类型会在真机上抛 InvalidCastException）
        if (data is Stream stream)
        {
            return new SnapshotEntry(format, SnapshotPayload.RawBytes, CopyStream(stream));
        }

        if (data is byte[] raw)
        {
            return new SnapshotEntry(format, SnapshotPayload.RawBytes, raw);
        }

        if (data is string text && PlainTextFormats.Contains(format))
        {
            return new SnapshotEntry(format, SnapshotPayload.Text, text);
        }

        // 其余一律按"原始 HGLOBAL 字节"抓。这里会命中两类：
        //   1) HTML Format / Rich Text Format —— WPF 当字符串读，但载荷是 UTF-8/ANSI 字节；
        //   2) WPF 认不出类型的自定义格式 —— 只要它确实是 HGLOBAL 就能原样带走。
        byte[]? bytes = TryReadRawFormat(format);
        if (bytes is { Length: > 0 })
        {
            return new SnapshotEntry(format, SnapshotPayload.RawBytes, bytes);
        }

        Log.Info($"剪切板快照跳过格式 {format}（类型 {data?.GetType().Name ?? "null"}，且不是可读的 HGLOBAL）");
        return null;
    }

    /// <summary>
    /// 按原始 HGLOBAL 读取某个格式的字节。
    /// 必须先确认句柄真的是全局内存块：CF_BITMAP / CF_PALETTE / CF_METAFILEPICT 给的是 GDI 句柄，
    /// 按 HGLOBAL 强读会拿到一段垃圾字节 —— 宁可跳过（那些格式另有 WPF 的位图通道负责）。
    /// </summary>
    private static byte[]? TryReadRawFormat(string format)
    {
        uint id = NativeMethods.RegisterClipboardFormat(format);
        if (id == 0)
        {
            return null;
        }

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            IntPtr handle = NativeMethods.GetClipboardData(id);
            if (handle == IntPtr.Zero || NativeMethods.GlobalFlags(handle) == NativeMethods.GMEM_INVALID_HANDLE)
            {
                return null;
            }

            int size = (int)NativeMethods.GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            IntPtr pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"按原始字节读取剪切板格式 {format} 失败", ex);
            return null;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// 位图拷成独立像素缓冲：快照要过几百毫秒才写回，
    /// 而剪切板来源的 BitmapSource 背后是 OLE 的共享内存（那时早就被换掉了）。
    /// </summary>
    private static BitmapSource CopyBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var copy = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        copy.Freeze();
        return copy;
    }

    private static byte[] CopyStream(Stream stream)
    {
        var memory = new MemoryStream();
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Describe(SnapshotEntry entry)
    {
        string detail = entry.Value switch
        {
            string text => $"{text.Length} 字符",
            string[] files => $"{files.Length} 个文件",
            BitmapSource bitmap => $"{bitmap.PixelWidth}×{bitmap.PixelHeight}",
            byte[] bytes => $"{bytes.Length} 字节",
            _ => entry.Value.GetType().Name,
        };

        return $"{entry.Format}({detail})";
    }

    /// <summary>快照里一条格式载荷的种类（决定写回时用哪种 WPF 数据类型）。</summary>
    private enum SnapshotPayload
    {
        /// <summary>WPF 认得的纯文本格式（UnicodeText / Text / OEMText）。</summary>
        Text,

        /// <summary>文件列表（FileDrop）。</summary>
        Files,

        /// <summary>位图（写回用 SetImage，由 WPF 生成正确的 DIB / BITMAP 载荷）。</summary>
        Bitmap,

        /// <summary>其余格式：原始字节（HGLOBAL 原始载荷或流载荷，统一存 byte[]），写回时包成内存流。</summary>
        RawBytes,
    }

    private sealed record SnapshotEntry(string Format, SnapshotPayload Payload, object Value);

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
