using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightClipboard.Interop;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>
/// 无界面自检（--selftest）：不依赖人工点击，验证存储、图片缓存、
/// 剪切板读写、拖拽数据对象构造以及 Win32 监听/热键注册是否可用。
/// </summary>
public static class SelfTest
{
    private static readonly List<string> Lines = new();
    private static int _passed;
    private static int _failed;

    public static int Run(string reportPath)
    {
        Lines.Clear();
        _passed = 0;
        _failed = 0;

        AttachConsole();

        Section("环境");
        Info($"数据目录: {AppPaths.Root}");
        Info($"运行目录: {AppContext.BaseDirectory}");
        Info($".NET 版本: {Environment.Version}");
        Info($"操作系统: {Environment.OSVersion.VersionString}");

        try
        {
            TestPaths();
            TestSettings();
            TestStorage();
            TestImageCache();
            TestClipboardRoundTrip();
            TestDragDataObject();
            TestWin32Listener();
            TestHotkey();
            TestKeyboardHook();
        }
        catch (Exception ex)
        {
            Fail("自检过程中出现未捕获异常", ex);
        }

        Section("结果");
        Info($"通过 {_passed} 项，失败 {_failed} 项");
        string verdict = _failed == 0 ? "SELFTEST: PASS" : "SELFTEST: FAIL";
        Lines.Add(verdict);

        WriteReport(reportPath);
        Console.WriteLine();
        Console.WriteLine(verdict);
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------

    private static void TestPaths()
    {
        Section("目录布局");
        AppPaths.EnsureCreated();
        Check("数据根目录存在", Directory.Exists(AppPaths.Root));
        Check("图片缓存目录存在", Directory.Exists(AppPaths.ImagesDirectory));
        Check("导出目录存在", Directory.Exists(AppPaths.ExportsDirectory));
        Check("日志目录存在", Directory.Exists(AppPaths.LogsDirectory));
    }

    private static void TestSettings()
    {
        Section("设置持久化");
        var service = new SettingsService();
        service.Load();
        service.Update(s =>
        {
            s.MaxItems = 123;
            s.AutoPaste = false;
            s.Theme = AppThemeMode.Dark;
        });

        var reloaded = new SettingsService();
        reloaded.Load();
        Check("MaxItems 往返一致", reloaded.Current.MaxItems == 123, $"值={reloaded.Current.MaxItems}");
        Check("AutoPaste 往返一致", !reloaded.Current.AutoPaste);
        Check("主题往返一致", reloaded.Current.Theme == AppThemeMode.Dark);
    }

    private static void TestStorage()
    {
        Section("LiteDB 存储");
        string dbPath = Path.Combine(AppPaths.Root, "selftest.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        using var storage = new StorageService(dbPath);
        Check("新建数据库", File.Exists(dbPath), dbPath);

        string text = "自检文本 😀 emoji 与中文混排";
        var item = new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            Text = text,
            Hash = ClipboardWriter.ComputeTextHash(text),
            ByteSize = Encoding.UTF8.GetByteCount(text),
            SourceApp = "selftest",
        };

        var (saved, isNew) = storage.AddOrTouch(item);
        Check("插入记录", isNew && saved.Id > 0, $"Id={saved.Id}");

        var (again, isNew2) = storage.AddOrTouch(new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            Text = text,
            Hash = ClipboardWriter.ComputeTextHash(text),
        });
        Check("相同内容去重（计数仍为 1）", !isNew2 && storage.Count() == 1, $"Count={storage.Count()}, Id={again.Id}");

        storage.SetPinned(saved.Id, true);
        Check("收藏标记", storage.GetById(saved.Id)?.IsPinned == true);

        var all = storage.GetAll();
        Check("查询返回记录", all.Count == 1 && all[0].Text == text);

        for (int i = 0; i < 8; i++)
        {
            storage.AddOrTouch(new ClipboardItem
            {
                Type = ClipboardItemType.Text,
                Text = "批量记录 " + i,
                Hash = ClipboardWriter.ComputeTextHash("批量记录 " + i),
            });
        }

        Check("批量写入后共 9 条", storage.Count() == 9, $"Count={storage.Count()}");

        var pruned = storage.Prune(3);
        Check(
            "超限裁剪（8 条非收藏裁剪到 3 条）",
            pruned.Count == 5 && storage.Count() == 4,
            $"裁剪={pruned.Count}, 剩余={storage.Count()}");
        Check("收藏项未被裁剪", storage.GetById(saved.Id) != null);

        var deleted = storage.Delete(saved.Id);
        Check("删除单条", deleted != null && storage.GetById(saved.Id) == null);
    }

    private static void TestImageCache()
    {
        Section("图片缓存与缩略图");
        var cache = new ImageCacheManager(320);
        var source = CreateTestBitmap(1920, 1080);

        var (path, hash, size) = cache.SavePng(source);
        Check("PNG 落盘", File.Exists(path), path);
        Check("哈希长度为 64", hash.Length == 64);
        Check("文件大小 > 0", size > 0, $"{size} 字节");

        byte[] head = File.ReadAllBytes(path).Take(8).ToArray();
        Check("PNG magic number 正确", head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

        // 相同内容二次保存应复用同一个文件（内容寻址）
        var (path2, hash2, _) = cache.SavePng(source);
        Check("相同图片复用同一文件", path2 == path && hash2 == hash);

        var thumb = cache.GetThumbnail(path);
        Check("缩略图可解码", thumb != null);
        Check("缩略图已按宽度缩放", thumb != null && thumb.PixelWidth <= 320, $"宽度={thumb?.PixelWidth}");
        Check("缩略图已冻结（可跨线程使用）", thumb?.IsFrozen == true);

        var full = cache.LoadFull(path);
        Check("原图可加载", full != null && full.PixelWidth == 1920, $"宽度={full?.PixelWidth}");

        string? export = cache.EnsureExportFile(path);
        Check("拖拽导出文件生成", export != null && File.Exists(export), export ?? "null");

        cache.DeleteImageFile(path);
        Check("删除图片文件", !File.Exists(path));
        Check("缩略图缓存可清理", cache.GetThumbnail(path) == null);
    }

    private static void TestClipboardRoundTrip()
    {
        Section("剪切板读写（文本 / Emoji / 图片）");

        const string sample = "LightClipboard 自检 🎉🚀❤️ emoji+中文+English";
        var writer = new ClipboardWriter(new ImageCacheManager(320));
        bool written = writer.SetText(sample);
        Check("写入文本到系统剪切板", written);
        Check("自身写入被标记为屏蔽（防死循环）", writer.IsSuppressed);

        string? readBack = TryGetClipboardText(out string? error);
        Check("读回文本一致", readBack == sample, readBack == null ? $"读取失败: {error}" : $"长度 {readBack.Length}");

        var parser = new ClipboardDataParser(new AppSettings());
        bool parsed = parser.TryRead(out var content);
        Check("解析器识别文本类型", parsed && content?.Type == ClipboardItemType.Text, $"Type={content?.Type}");
        Check("解析器保留 Emoji 原样", content?.Text == sample);

        var bitmap = CreateTestBitmap(640, 360);
        bool imageWritten = writer.SetImage(bitmap, null, includeFilePath: false);
        Check("写入图片到系统剪切板", imageWritten);

        bool imageParsed = parser.TryRead(out var imageContent);
        Check("解析器识别图片类型", imageParsed && imageContent?.Type == ClipboardItemType.Image, $"Type={imageContent?.Type}");
        Check(
            "读回图片尺寸一致",
            imageContent?.Image != null && imageContent.Image.PixelWidth == 640 && imageContent.Image.PixelHeight == 360,
            $"{imageContent?.Image?.PixelWidth}x{imageContent?.Image?.PixelHeight}");

        if (imageContent?.Image != null)
        {
            var opaque = ClipboardDataParser.MakeOpaque(imageContent.Image);
            byte[] pixels = new byte[opaque.PixelWidth * 4];
            opaque.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, opaque.PixelWidth * 4, 0);
            Check("DIB 回读后 alpha 已被修正为不透明", pixels[3] == 255, $"alpha={pixels[3]}");
        }

        // 回归保护：剪切板取回的图片必须先规范化，才能在线程池里编码。
        // 否则每次截图都会退化成 UI 线程同步编码（界面卡顿）。
        if (imageContent?.Image != null)
        {
            var normalized = ImageCacheManager.Normalize(imageContent.Image, preserveAlpha: false);
            bool backgroundEncode = RunOnBackgroundThread(() => ImageCacheManager.EncodePng(normalized));
            Check("规范化后的剪切板图片可在后台线程编码", backgroundEncode);
        }
    }

    /// <summary>在线程池线程上执行一段代码，返回是否成功。</summary>
    private static bool RunOnBackgroundThread(Action action)
    {
        try
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                action();
            }).Wait(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            Warn($"后台线程执行失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void TestDragDataObject()
    {
        Section("拖拽数据对象（FileDrop + Bitmap + PNG）");
        var images = new ImageCacheManager(320);
        var dragDrop = new DragDropService(images);

        var source = CreateTestBitmap(800, 600);
        var (path, hash, size) = images.SavePng(source);
        var imageItem = new ClipboardItem
        {
            Type = ClipboardItemType.Image,
            ImagePath = path,
            Hash = hash,
            ByteSize = size,
            ImageWidth = 800,
            ImageHeight = 600,
        };

        var data = dragDrop.BuildDataObject(imageItem);
        Check("图片条目可构造拖拽数据", data != null);
        Check("包含 FileDrop 格式", data?.GetDataPresent(DataFormats.FileDrop) == true);
        Check("包含 Bitmap 格式", data?.GetDataPresent(DataFormats.Bitmap) == true);
        Check("包含 PNG 格式", data?.GetDataPresent("PNG") == true);

        if (data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            Check("FileDrop 指向真实存在的 PNG 文件", files.Length == 1 && File.Exists(files[0]), files.FirstOrDefault() ?? "空");
        }

        var textItem = new ClipboardItem { Type = ClipboardItemType.Text, Text = "拖拽文本 😀" };
        var textData = dragDrop.BuildDataObject(textItem);
        Check("文本条目拖拽数据包含 UnicodeText", textData?.GetDataPresent(DataFormats.UnicodeText) == true);
    }

    private static void TestWin32Listener()
    {
        Section("Win32 剪切板监听 (AddClipboardFormatListener)");
        var parameters = new HwndSourceParameters("LightClipboard.SelfTest")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };

        using var source = new HwndSource(parameters);
        bool added = NativeMethods.AddClipboardFormatListener(source.Handle);
        Check("注册剪切板格式监听", added, added ? "OK" : $"Win32 错误码 {Marshal.GetLastWin32Error()}");
        bool removed = NativeMethods.RemoveClipboardFormatListener(source.Handle);
        Check("注销剪切板格式监听", removed);
    }

    private static void TestHotkey()
    {
        Section("全局热键 (RegisterHotKey)");
        var parameters = new HwndSourceParameters("LightClipboard.SelfTest.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),
        };

        using var source = new HwndSource(parameters);
        const int id = 0x7A11;
        uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        bool ok = NativeMethods.RegisterHotKey(source.Handle, id, modifiers, NativeMethods.VK_V);

        if (ok)
        {
            Check("Ctrl+Shift+V 可注册", true);
            NativeMethods.UnregisterHotKey(source.Handle, id);
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            // 已被本机其它程序（如 Chrome 的“粘贴为纯文本”）占用时会失败，此时程序会自动降级
            Warn($"Ctrl+Shift+V 注册失败（错误码 {error}），程序运行时会自动降级到 Ctrl+Alt+V");
            Check("热键注册流程未抛异常", true);
        }
    }

    private static void TestKeyboardHook()
    {
        Section("Win+V 低级键盘钩子 (WH_KEYBOARD_LL)");
        using var hook = new KeyboardHookService();

        bool started = hook.Start();
        Check("挂载低级键盘钩子", started, started ? "OK" : $"Win32 错误码 {hook.LastError}");
        Check("挂载后处于运行状态", hook.IsRunning);

        hook.Stop();
        Check("卸载后回到未运行状态", !hook.IsRunning);
    }

    // ------------------------------------------------------------------

    private static BitmapSource CreateTestBitmap(int width, int height)
    {
        int stride = width * 4;
        var buffer = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                buffer[offset + 0] = (byte)(40 + 180 * x / width);      // B
                buffer[offset + 1] = (byte)(90 + 120 * y / height);     // G
                buffer[offset + 2] = (byte)(200 - 120 * y / height);    // R
                buffer[offset + 3] = 255;                               // A
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static string? TryGetClipboardText(out string? error)
    {
        error = null;
        try
        {
            return Clipboard.GetText();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void Section(string title)
    {
        Lines.Add(string.Empty);
        Lines.Add("== " + title + " ==");
    }

    private static void Info(string message) => Lines.Add("   " + message);

    private static void Warn(string message)
    {
        Lines.Add("  ! " + message);
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }

        string suffix = string.IsNullOrEmpty(detail) ? string.Empty : $"  ({detail})";
        Lines.Add($"  [{(ok ? "PASS" : "FAIL")}] {name}{suffix}");
    }

    private static void Fail(string name, Exception ex)
    {
        _failed++;
        Lines.Add($"  [FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        Lines.Add(ex.StackTrace ?? string.Empty);
    }

    private static void AttachConsole()
    {
        try
        {
            if (NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
            }
        }
        catch
        {
            // 没有父控制台时忽略
        }
    }

    private static void WriteReport(string reportPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllLines(reportPath, Lines, new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }

        foreach (string line in Lines)
        {
            Console.WriteLine(line);
        }
    }
}
