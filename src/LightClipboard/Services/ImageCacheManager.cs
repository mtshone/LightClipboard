using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightClipboard.Services;

/// <summary>
/// 图片缓存管理：
///  - 原始位图第一时间落盘为 PNG，避免长期占用内存；
///  - 界面上只使用 DecodePixelWidth 限制过的缩略图；
///  - 使用固定容量的 LRU 缓存复用已解码缩略图，防止内存无限增长。
/// </summary>
public sealed class ImageCacheManager
{
    private const int MaxThumbnailCacheEntries = 120;

    private readonly Dictionary<string, BitmapImage> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _gate = new();
    private int _thumbnailWidth = 320;

    public ImageCacheManager(int thumbnailWidth)
    {
        _thumbnailWidth = Math.Clamp(thumbnailWidth, 96, 1024);
    }

    public int ThumbnailWidth
    {
        get => _thumbnailWidth;
        set
        {
            int clamped = Math.Clamp(value, 96, 1024);
            if (clamped == _thumbnailWidth)
            {
                return;
            }

            _thumbnailWidth = clamped;
            ClearThumbnailCache();
        }
    }

    /// <summary>
    /// 把任意位图规范化为“完全冻结的普通 BitmapSource”。
    /// 剪切板取回的 BitmapFrame 内部仍持有与 UI 线程绑定的解码器对象，即便调用过
    /// Freeze()，在线程池里访问依然会抛 InvalidOperationException。先在这里（UI 线程）
    /// 拷贝成纯像素位图，PNG 编码才能安全地放到后台线程执行。
    /// </summary>
    public static BitmapSource Normalize(BitmapSource source, bool preserveAlpha)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        if (!preserveAlpha)
        {
            for (int i = 3; i < buffer.Length; i += 4)
            {
                buffer[i] = 255;
            }
        }

        var result = BitmapSource.Create(
            width,
            height,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            buffer,
            stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// 把位图保存为本地 PNG 文件（按内容哈希命名，天然去重）。
    /// </summary>
    /// <returns>文件绝对路径、内容哈希、文件字节数。</returns>
    public (string Path, string Hash, long ByteSize) SavePng(BitmapSource source)
    {
        byte[] png = EncodePng(source);
        string hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();

        string folder = System.IO.Path.Combine(AppPaths.ImagesDirectory, DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(folder);

        string path = System.IO.Path.Combine(folder, hash[..24] + ".png");
        if (!File.Exists(path))
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, path, overwrite: true);
        }

        return (path, hash, png.LongLength);
    }

    /// <summary>把位图编码为 PNG 字节数组。</summary>
    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 取得（带缓存的）缩略图。必须在 UI 线程调用。
    /// </summary>
    public BitmapImage? GetThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        lock (_gate)
        {
            if (_thumbnails.TryGetValue(path, out var cached))
            {
                _lru.Remove(path);
                _lru.AddFirst(path);
                return cached;
            }
        }

        BitmapImage? image = Decode(path, _thumbnailWidth);
        if (image == null)
        {
            return null;
        }

        lock (_gate)
        {
            _thumbnails[path] = image;
            _lru.AddFirst(path);
            while (_lru.Count > MaxThumbnailCacheEntries)
            {
                string oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _thumbnails.Remove(oldest);
            }
        }

        return image;
    }

    /// <summary>按原始尺寸加载图片（用于“另存为”/拖拽导出等需要全分辨率的场景）。</summary>
    public BitmapSource? LoadFull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Warn($"加载图片失败: {path}", ex);
            return null;
        }
    }

    private static BitmapImage? Decode(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立即读入内存并释放文件句柄
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;           // 只解码缩略图尺寸，避免内存暴涨
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();                                 // 冻结后可跨线程安全使用
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"解码缩略图失败: {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// 为拖拽准备一个稳定的本地 PNG 文件路径（同名复用，便于聊天软件识别为同一文件）。
    /// </summary>
    public string? EnsureExportFile(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ExportsDirectory);
            string name = System.IO.Path.GetFileName(imagePath);
            string target = System.IO.Path.Combine(AppPaths.ExportsDirectory, name);
            if (!File.Exists(target))
            {
                File.Copy(imagePath, target, overwrite: false);
            }

            return target;
        }
        catch (Exception ex)
        {
            Log.Warn("导出图片文件失败", ex);
            return imagePath;
        }
    }

    /// <summary>删除磁盘上的图片文件。</summary>
    public void DeleteImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            if (_thumbnails.Remove(path, out _))
            {
                _lru.Remove(path);
            }
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除图片失败: {path}", ex);
        }
    }

    public void ClearThumbnailCache()
    {
        lock (_gate)
        {
            _thumbnails.Clear();
            _lru.Clear();
        }
    }

    /// <summary>清理没有任何记录引用的孤儿图片，以及 7 天前的拖拽导出文件。</summary>
    public void CollectGarbage(IEnumerable<string?> referencedPaths)
    {
        var referenced = new HashSet<string>(
            referencedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(AppPaths.ImagesDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(AppPaths.ImagesDirectory, "*.png", SearchOption.AllDirectories))
                {
                    if (!referenced.Contains(file))
                    {
                        DeleteImageFile(file);
                    }
                }
            }

            if (Directory.Exists(AppPaths.ExportsDirectory))
            {
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (string file in Directory.EnumerateFiles(AppPaths.ExportsDirectory, "*.png"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("清理图片缓存失败", ex);
        }
    }
}
