using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using LightClipboard.Selection;
using LightClipboard.ViewModels;

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
            TestOutputKeepsOrder();
            TestOutputRestoresClipboard();
            TestImageCache();
            TestClipboardRoundTrip();
            TestDragDataObject();
            TestMultiImageDragData();
            TestClipboardSelection();
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
            s.KeepOnTopWhenUnfocused = true;
            s.Theme = AppThemeMode.Dark;
        });

        var reloaded = new SettingsService();
        reloaded.Load();
        Check("MaxItems 往返一致", reloaded.Current.MaxItems == 123, $"值={reloaded.Current.MaxItems}");
        Check("AutoPaste 往返一致", !reloaded.Current.AutoPaste);
        Check("始终置顶往返一致", reloaded.Current.KeepOnTopWhenUnfocused);
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

    /// <summary>
    /// 回归保护：面板"输出"（单击卡片复制 / 粘贴）不得改写排序键，也不得把卡片移回列表顶部 ——
    /// 历史顺序必须稳定（改动落点在 MainViewModel.CopyToClipboard）。
    /// 后半段是反向对照：入库（含去重命中）依旧刷新时间戳并置顶，
    /// 证明"输出后顺序不变"不是因为排序被写死了。
    /// </summary>
    private static void TestOutputKeepsOrder()
    {
        Section("输出（复制 / 粘贴）不改变历史排序位置");

        string dbPath = Path.Combine(AppPaths.Root, "selftest-order.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        using var storage = new StorageService(dbPath);
        var images = new ImageCacheManager(320);
        var writer = new ClipboardWriter(images);
        var settings = new SettingsService();
        settings.Load();
        using var hook = new KeyboardHookService();

        // 三条记录、时间戳依次拉开：列表顺序应为 最新 → 最旧
        ClipboardItem Add(string text, DateTime time) => storage.AddOrTouch(new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            Text = text,
            Hash = ClipboardWriter.ComputeTextHash(text),
            ByteSize = Encoding.UTF8.GetByteCount(text),
            SourceApp = "selftest",
            CreatedAt = time,
            LastUsedAt = time,
        }).Item;

        var newest = Add("输出排序 · 最新", DateTime.Now.AddMinutes(-1));
        var middle = Add("输出排序 · 中间", DateTime.Now.AddMinutes(-2));
        var oldest = Add("输出排序 · 最旧", DateTime.Now.AddMinutes(-3));

        var vm = new MainViewModel(storage, images, writer, settings, hook);
        vm.Load();
        Check(
            "装载顺序 = 最近入库倒序",
            vm.Items.Count == 3
                && vm.Items[0].Id == newest.Id
                && vm.Items[1].Id == middle.Id
                && vm.Items[2].Id == oldest.Id,
            $"Count={vm.Items.Count}");

        // 输出列表最下面那条：改动前它会跳到最前
        var target = vm.Items[vm.Items.Count - 1];
        DateTime lastUsedBefore = storage.GetById(oldest.Id)!.LastUsedAt;
        vm.ActivateCommand.Execute(target);

        string? clipboard = TryGetClipboardText(out string? clipboardError);
        Check(
            "输出确实发生（该条内容已写入剪切板）",
            clipboard == oldest.Text,
            clipboard == null ? $"读取失败: {clipboardError}" : $"读回 {clipboard.Length} 字符");

        Check(
            "输出后卡片没有被移到最前",
            vm.Items.Count == 3 && ReferenceEquals(vm.Items[vm.Items.Count - 1], target),
            $"首条 = {vm.Items[0].Title}");
        Check(
            "输出后排序键未被改写",
            storage.GetById(oldest.Id)!.LastUsedAt == lastUsedBefore,
            $"{lastUsedBefore:HH:mm:ss.fff} → {storage.GetById(oldest.Id)!.LastUsedAt:HH:mm:ss.fff}");

        // 反向对照：DateTime.Now 的精度约 15.6ms，睡一下才观察得到变化
        System.Threading.Thread.Sleep(30);
        Add(oldest.Text!, DateTime.Now);
        Check(
            "反向对照：再次收录相同内容仍刷新排序键",
            storage.GetById(oldest.Id)!.LastUsedAt > lastUsedBefore);

        vm.Load();
        Check("反向对照：该条按新时间排到最前", vm.Items[0].Id == oldest.Id, $"首条 = {vm.Items[0].Title}");
    }

    /// <summary>
    /// 回归保护：面板"输出"只是把系统剪切板**借一次** —— 自动粘贴送达后必须还原成输出前的内容，
    /// 用户随后按 Ctrl+V 得到的仍是那一条（正常情形就是列表顶部那条），而不是刚被输出的这条；
    /// 列表顺序同样不受影响（与 <see cref="TestOutputKeepsOrder"/> 是同一件事的两半）。
    /// 与"只复制"路径（内容刻意留在剪切板里等用户自己 Ctrl+V）构成反向对照。
    /// 落点：MainViewModel.CopyToClipboard / OnEmojiPicked → ClipboardWriter.CaptureForRestore / RestoreNow。
    /// </summary>
    private static void TestOutputRestoresClipboard()
    {
        Section("输出后剪切板还原（借一次就还）");

        string dbPath = Path.Combine(AppPaths.Root, "selftest-restore.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        using var storage = new StorageService(dbPath);
        var images = new ImageCacheManager(320);
        var writer = new ClipboardWriter(images);
        var settings = new SettingsService();
        settings.Load();

        // 本组验的是"点卡片 → 自动粘贴"这一档，显式打开自动粘贴（前面的设置组把它写成了 false）
        settings.Update(s => s.AutoPaste = true);

        using var hook = new KeyboardHookService();
        var vm = new MainViewModel(storage, images, writer, settings, hook);

        const string previous = "输出前的内容（= 列表顶部那条）";
        const string target = "被输出的那条（列表第二条）";
        const string older = "更旧的一条（列表第三条）";

        ClipboardItem Add(string text, DateTime time) => storage.AddOrTouch(new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            Text = text,
            Hash = ClipboardWriter.ComputeTextHash(text),
            ByteSize = Encoding.UTF8.GetByteCount(text),
            SourceApp = "selftest",
            CreatedAt = time,
            LastUsedAt = time,
        }).Item;

        var top = Add(previous, DateTime.Now.AddMinutes(-1));
        var middle = Add(target, DateTime.Now.AddMinutes(-2));
        Add(older, DateTime.Now.AddMinutes(-3));

        vm.Load();
        Check(
            "装载顺序 = 最近入库倒序（输出前那份在列表顶部）",
            vm.Items.Count == 3 && vm.Items[0].Id == top.Id && vm.Items[1].Id == middle.Id,
            $"首条 = {vm.Items.FirstOrDefault()?.Title}");

        // 前置：剪切板里是"输出前的内容"（真实场景里它由外部复制写入，也就是列表顶部那条）
        writer.SetText(previous);
        Check("前置：剪切板已是输出前的内容", TryGetClipboardText(out _) == previous, TryGetClipboardText(out _));

        // 点列表第二条：借走 → 写入该条 → 粘贴送达 → 还原
        var clicked = vm.Items[1];
        vm.ActivateCommand.Execute(clicked);

        Check("输出确实发生（剪切板被借走，写的是被点的那条）", TryGetClipboardText(out _) == target, TryGetClipboardText(out _));
        Check(
            "输出后列表顺序不变（第二条仍在原位）",
            vm.Items.Count == 3 && ReferenceEquals(vm.Items[1], clicked),
            $"首条 = {vm.Items[0].Title}");
        Check("输出后有待还原的快照", writer.HasRestorePending);

        bool restored = writer.RestoreNow();

        Check("粘贴送达后确实执行了还原", restored);
        Check(
            "还原后剪切板 = 输出前的内容（按 Ctrl+V 得到列表顶部那条）",
            TryGetClipboardText(out _) == previous,
            TryGetClipboardText(out _));
        Check("还原后剪切板不是被输出的那条", TryGetClipboardText(out _) != target);
        Check(
            "还原后列表顺序仍不变",
            vm.Items.Count == 3 && ReferenceEquals(vm.Items[1], clicked),
            $"首条 = {vm.Items[0].Title}");
        Check("还原后没有残留快照", !writer.HasRestorePending);

        // 反向对照 1：只复制不粘贴 → 内容就该留在剪切板里（这条路径下一步就是用户自己按 Ctrl+V）
        settings.Update(s => s.AutoPaste = false);
        vm.ActivateCommand.Execute(vm.Items[2]);
        Check("反向对照：只复制路径把内容留在剪切板里", TryGetClipboardText(out _) == older, TryGetClipboardText(out _));
        Check("反向对照：只复制路径不建立快照", !writer.HasRestorePending);

        // 反向对照 2：没有快照时还原是空操作 —— 绝不能顺手把剪切板清空
        Check(
            "反向对照：无快照时还原为空操作",
            !writer.RestoreNow() && TryGetClipboardText(out _) == older,
            TryGetClipboardText(out _));

        // 多格式内容：HTML Format 这类"WPF 当字符串读、载荷其实是字节"的格式必须按原始字节带走，
        // 否则还原时会被字符串通道改写（真机实测 Rich Text Format 会被整段吃成空串，见 CHANGELOG v1.2.4）
        const string rich = "富文本内容：<b>还原后要一字不差</b>";
        var richData = new DataObject();
        richData.SetData(DataFormats.UnicodeText, rich);
        richData.SetData(DataFormats.Text, rich);
        richData.SetData("HTML Format", rich);

        bool richWritten;
        try
        {
            Clipboard.SetDataObject(richData, copy: true);
            richWritten = true;
        }
        catch (Exception ex)
        {
            richWritten = false;
            Warn($"写入多格式剪切板失败: {ex.Message}");
        }

        Check("多格式：富文本剪切板写入成功", richWritten);
        byte[]? htmlBefore = ReadRawClipboardFormat("HTML Format");

        Check("多格式：快照抓取成功", writer.CaptureForRestore());
        writer.SetText("输出的内容");
        writer.RestoreNow();

        Check("多格式：还原后文本一字不差", TryGetClipboardText(out _) == rich, TryGetClipboardText(out _));

        byte[]? htmlAfter = ReadRawClipboardFormat("HTML Format");
        Check(
            "多格式：HTML Format 逐字节一致",
            htmlBefore is { Length: > 0 } && htmlAfter != null && htmlBefore.AsSpan().SequenceEqual(htmlAfter),
            htmlBefore == null || htmlAfter == null ? "读取失败" : $"{htmlBefore.Length} → {htmlAfter.Length} 字节");

        // 图片：快照要同时带走位图（BitmapSource 通道）与 PNG 流（RawBytes 通道）。
        // 真机端到端第一次跑就抓到过"流载荷没转成 byte[]、还原时强转抛 InvalidCastException"，
        // 这条是那次的回归保护 —— 纯文本用例永远覆盖不到它。
        var image = CreateTestBitmap(48, 32);
        var imageData = new DataObject();
        imageData.SetImage(image);
        imageData.SetData("PNG", new MemoryStream(ImageCacheManager.EncodePng(image)));

        bool imageWritten;
        try
        {
            Clipboard.SetDataObject(imageData, copy: true);
            imageWritten = true;
        }
        catch (Exception ex)
        {
            imageWritten = false;
            Warn($"写入图片剪切板失败: {ex.Message}");
        }

        Check("图片：图片剪切板写入成功", imageWritten);
        byte[]? pngBefore = ReadRawClipboardFormat("PNG");
        Check("图片：快照抓取成功", writer.CaptureForRestore());

        writer.SetText("输出的内容（图片用例）");
        Check("图片：还原执行成功（流载荷不再被当成 byte[] 强转）", writer.RestoreNow());

        var restoredImage = Clipboard.GetImage();
        Check(
            "图片：还原后仍能读到图片且尺寸一致",
            restoredImage != null && restoredImage.PixelWidth == 48 && restoredImage.PixelHeight == 32,
            restoredImage == null ? "null" : $"{restoredImage.PixelWidth}x{restoredImage.PixelHeight}");

        byte[]? pngAfter = ReadRawClipboardFormat("PNG");
        Check(
            "图片：PNG 流逐字节一致",
            pngBefore is { Length: > 0 } && pngAfter != null && pngBefore.AsSpan().SequenceEqual(pngAfter),
            pngBefore == null || pngAfter == null ? "读取失败" : $"{pngBefore.Length} → {pngAfter.Length} 字节");
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

    /// <summary>
    /// 多选拖拽的数据对象：只用 FileDrop（N 个导出 PNG），顺序 = 传入顺序，
    /// 非图片条目被跳过，一张都导不出时返回 null。
    /// </summary>
    private static void TestMultiImageDragData()
    {
        Section("多选拖拽：多张图片的数据对象");
        var images = new ImageCacheManager(320);
        var dragDrop = new DragDropService(images);

        // 三张尺寸不同的测试图 → 内容哈希不同 → 导出文件名必然不同
        // （ImagePath 是内容寻址的，同一次多选里不会出现重复路径）
        var imageItems = new List<ClipboardItem>();
        for (int i = 0; i < 3; i++)
        {
            int width = 320 + i * 32;
            var bitmap = CreateTestBitmap(width, 200);
            var (path, hash, size) = images.SavePng(bitmap);
            imageItems.Add(new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                ImagePath = path,
                Hash = hash,
                ByteSize = size,
                ImageWidth = width,
                ImageHeight = 200,
            });
        }

        var multi = dragDrop.BuildDataObjectForImages(imageItems);
        Check("多图拖拽数据已构造", multi != null);
        Check("多图拖拽包含 FileDrop", multi?.GetDataPresent(DataFormats.FileDrop) == true);

        var dropped = multi?.GetData(DataFormats.FileDrop) as string[];
        Check("多图拖拽路径数量 = 3", dropped?.Length == 3, $"数量={dropped?.Length ?? 0}");
        Check("多图拖拽路径全部真实存在", dropped != null && dropped.All(File.Exists));

        // “流式队列”的语义就靠顺序断言保住：资源管理器里的文件顺序 = 传入顺序
        //（面板给出的拖出队列顺序：有 Ctrl 参与时 = 点击顺序，纯 Shift 时 = 列表顺序）
        Check(
            "多图拖拽顺序与传入一致",
            dropped != null
                && dropped.Select(Path.GetFileName)
                    .SequenceEqual(imageItems.Select(i => Path.GetFileName(i.ImagePath))),
            string.Join(", ", dropped?.Select(Path.GetFileName) ?? Array.Empty<string>()));

        // 防御：调用方漏过滤时，非图片条目绝不能混进 FileDrop 队列
        var mixed = dragDrop.BuildDataObjectForImages(new List<ClipboardItem>
        {
            imageItems[0],
            new() { Type = ClipboardItemType.Text, Text = "多选不含文本" },
        });
        Check(
            "非图片条目被跳过（1 图 + 1 文本 → 1 个路径）",
            (mixed?.GetData(DataFormats.FileDrop) as string[])?.Length == 1);

        // 一张都导不出 → null → DoDragDrop 返回 None → 面板保留
        var noImages = dragDrop.BuildDataObjectForImages(new List<ClipboardItem>
        {
            new() { Type = ClipboardItemType.Text, Text = "没有图片" },
        });
        Check("没有可导出的图片时返回 null", noImages == null);
    }

    /// <summary>
    /// 多选状态机：手势无法自动化，但状态机可以完全无界面覆盖。
    /// 覆盖 Ctrl 切换 / Shift 范围跨类型 / 锚点规则 / Reset 与 Insert 的差别 /
    /// 拖拽队列顺序（有 Ctrl 参与 = 点击顺序，纯 Shift = 列表顺序，Shift 不重排也不取消已选项）。
    /// </summary>
    private static void TestClipboardSelection()
    {
        Section("多选状态机（ClipboardSelection）");
        var images = new ImageCacheManager(320);
        var dragDrop = new DragDropService(images);
        var items = new ObservableCollection<ClipboardItemViewModel>();
        var selection = new ClipboardSelection(items);

        int changedCount = 0;
        selection.Changed += (_, _) => changedCount++;

        // 列表形态刻意排成：0 图片 / 1 文本 / 2 图片 / 3 文件 / 4 图片
        // —— 正好造出"Shift 范围跨越文本与文件卡片"的场景
        ClipboardItemViewModel MakeImage(int width)
        {
            var (path, hash, size) = images.SavePng(CreateTestBitmap(width, 200));
            var vm = new ClipboardItemViewModel(new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                ImagePath = path,
                Hash = hash,
                ByteSize = size,
                ImageWidth = width,
                ImageHeight = 200,
            });
            items.Add(vm);
            return vm;
        }

        var image0 = MakeImage(320);
        var text1 = new ClipboardItemViewModel(new ClipboardItem { Type = ClipboardItemType.Text, Text = "中间的文本 😀" });
        items.Add(text1);
        var image2 = MakeImage(400);
        var files3 = new ClipboardItemViewModel(new ClipboardItem { Type = ClipboardItemType.Files, FilePaths = new List<string> { Path.Combine(Path.GetTempPath(), "a.txt") } });
        items.Add(files3);
        var image4 = MakeImage(480);

        // ---------- Ctrl 单击：逐张增减 ----------
        Check("Ctrl 点击图片加入选择", selection.ToggleImage(image0) && selection.Count == 1);
        Check("Ctrl 再点同一张取消选择", selection.ToggleImage(image0) && selection.Count == 0);
        Check(
            "Ctrl 点击非图片被拒绝且不改动已有选择",
            !selection.ToggleImage(text1) && !selection.ToggleImage(files3) && selection.Count == 0);

        // ---------- Shift 范围：跨文本 / 文件时只选图片 ----------
        selection.SelectSingleImage(image0);   // 普通单击：清空多选，把 0 号设为锚点
        Check("普通单击清空已有选择", selection.Count == 0);

        selection.SelectRangeImages(image2);
        Check(
            "Shift 范围跨文本只选图片（0..2 → 2 张，文本被跳过）",
            selection.Count == 2 && selection.IsSelected(image0) && selection.IsSelected(image2) && !selection.IsSelected(text1),
            $"Count={selection.Count}");

        selection.SelectRangeImages(image4);
        Check(
            "Shift 跨到 4 号：区间内 3 张图片全选中，文本与文件被跳过",
            selection.Count == 3 && !selection.IsSelected(files3) && selection.Selected.All(v => v.IsImage),
            $"Count={selection.Count}");

        selection.SelectRangeImages(image2);
        Check("Shift 连续点击以锚点为基准反复调整范围（锚点不动）", selection.Count == 2, $"Count={selection.Count}");

        // ---------- 锚点被自己取消后必须回退，而不是留悬空锚点 ----------
        selection.Clear();
        selection.SelectSingleImage(image2);   // 锚点 = 2 号
        selection.ToggleImage(image2);         // 选中 2 号（锚点仍是 2 号）
        selection.ToggleImage(image4);         // 选中 4 号，锚点移到 4 号
        selection.ToggleImage(image4);         // 取消的正好是锚点 → 回退到队列末尾的 2 号
        selection.SelectRangeImages(image0);   // 以 2 号为锚点跨到 0 号 → 图片 0、2
        Check(
            "锚点被取消后回退到剩余已选项（Shift 范围仍可用）",
            selection.Count == 2 && selection.IsSelected(image0) && selection.IsSelected(image2) && !selection.IsSelected(image4),
            $"Count={selection.Count}");

        // ---------- 列表重建：Reset 清空 / Insert 不清空 ----------
        items.Clear();
        Check("列表整体重建（Reset）自动清空选择", selection.Count == 0, $"Count={selection.Count}");

        items.Add(image0);
        items.Add(text1);
        items.Add(image2);
        items.Add(files3);
        items.Add(image4);
        selection.SelectSingleImage(image0);
        selection.SelectRangeImages(image4);
        int beforeInsert = selection.Count;
        items.Insert(0, new ClipboardItemViewModel(new ClipboardItem { Type = ClipboardItemType.Text, Text = "刚入库的新文本" }));
        Check(
            "列表增量变更（Insert）不清空选择",
            beforeInsert == 3 && selection.Count == 3,
            $"插入前 {beforeInsert} → 插入后 {selection.Count}");

        // ---------- 拖拽队列：有 Ctrl 参与 ⇒ 点击顺序；纯 Shift ⇒ 列表顺序 ----------
        selection.Clear();
        selection.ToggleImage(image4);   // 故意倒着点：4 → 2 → 0
        selection.ToggleImage(image2);
        selection.ToggleImage(image0);

        // 队列断言的两个小工具：期望值写成"按顺序列出的卡片"，失败时打印实际队列顺序
        bool QueueIs(params ClipboardItemViewModel[] expected) =>
            selection.BuildDragItems()
                .Select(i => i.ImagePath)
                .SequenceEqual(expected.Select(v => v.Model.ImagePath));

        string QueueOrder() => string.Join(
            " | ",
            selection.BuildDragItems().Select(i => Path.GetFileName(i.ImagePath ?? string.Empty)));

        var dragItems = selection.BuildDragItems();
        Check("拖拽队列长度 = 已选图片数", dragItems.Count == 3, $"Count={dragItems.Count}");
        Check("拖拽队列只含图片条目", dragItems.All(i => i.Type == ClipboardItemType.Image));
        Check(
            "Ctrl 点击顺序 4→2→0 ⇒ 拖拽队列顺序 = image4,image2,image0（点击顺序，不再是列表顺序）",
            dragItems.Select(i => i.ImagePath)
                .SequenceEqual(new[] { image4.Model.ImagePath, image2.Model.ImagePath, image0.Model.ImagePath }),
            string.Join(" | ", dragItems.Select(i => Path.GetFileName(i.ImagePath ?? string.Empty))));

        // 契约：取消后重新 Ctrl 选中 → 追加到队列**末尾**（不是回到它原来的位置）
        selection.ToggleImage(image2);   // 取消 2 号 → 队列 4,0
        selection.ToggleImage(image2);   // 重新选中 2 号 → 排到末尾 → 4,0,2
        Check("取消后重新 Ctrl 选中排到队列末尾（4,0 → 4,0,2）", QueueIs(image4, image0, image2), QueueOrder());

        // 契约：Ctrl 点非图片被拒绝 → 队列内容与顺序一项都不动
        string beforeReject = QueueOrder();
        bool textAccepted = selection.ToggleImage(text1);
        bool filesAccepted = selection.ToggleImage(files3);
        Check(
            "Ctrl 点非图片被拒绝且拖拽队列与顺序都不变",
            !textAccepted && !filesAccepted && QueueOrder() == beforeReject,
            $"队列={QueueOrder()}");

        // 契约：纯 Shift（全程没有 Ctrl 参与）⇒ 队列按列表升序；且区间整体替换（连续 Shift 会收缩）
        selection.Clear();
        selection.SelectSingleImage(image0);   // 普通单击：锚点 = 0 号、多选清空
        selection.SelectRangeImages(image4);   // 0..4 区间里的图片 = 0、2、4
        Check("纯 Shift 区间队列按列表升序（image0,image2,image4）", QueueIs(image0, image2, image4), QueueOrder());

        selection.SelectRangeImages(image2);   // 回到更小的区间 → 整体替换
        Check("纯 Shift 再 Shift 回更小区间 ⇒ 整体替换并收缩为 image0,image2", QueueIs(image0, image2), QueueOrder());

        // 契约：混合 Ctrl + Shift —— 区间集合变了，但整体仍按点击顺序（Shift 只负责"选哪些"，不负责排序）
        selection.Clear();
        selection.ToggleImage(image4);         // 锚点 = 4 号
        selection.ToggleImage(image2);         // 锚点 = 2 号
        selection.SelectRangeImages(image0);   // 区间里的图片 = 0、2；2 已在队列里 → 只把 0 追加到末尾
        Check("Ctrl 之后 Shift 扩选 ⇒ 队列整体保持点击顺序（image4,image2,image0）", QueueIs(image4, image2, image0), QueueOrder());

        // 契约：集合与顺序跟现状完全一致时再 Shift ⇒ 不重置点击序（"集合未变不重置"这条关键规则）
        selection.SelectRangeImages(image0);
        Check(
            "同集合再 Shift 一次不重置点击序（仍是 image4,image2,image0）",
            QueueIs(image4, image2, image0) && selection.Count == 3,
            QueueOrder());

        // 契约：混合扩张 —— Shift 到一个更靠下的图片（新增的 5 号）⇒ 新项追加到末尾，已有项不重排
        var image5 = MakeImage(560);
        selection.SelectRangeImages(image5);   // 锚点 2 号 → 5 号：区间里只有 5 号还没进队列
        Check(
            "Ctrl 之后 Shift 扩到更靠下的图片 ⇒ 新项追加到队列末尾（image4,image2,image0,image5）",
            QueueIs(image4, image2, image0, image5),
            QueueOrder());

        // 契约：混合收缩 —— 再 Shift 回更小的区间 ⇒ 已在队列里的项一项都不移除（Shift 不负责取消）
        selection.SelectRangeImages(image0);   // 区间 0..2 里的图片都已进队列 → 只做"集合未变"的空操作
        Check(
            "Ctrl 之后 Shift 回更小区间 ⇒ 已在队列里的项一项都不移除（仍是 4 张、顺序不变）",
            QueueIs(image4, image2, image0, image5) && selection.Count == 4,
            QueueOrder());

        // 复位成"Ctrl 点击顺序 4→2→0"：下面两条边界断言依赖"当前队列里就是这 3 张"
        selection.Clear();
        selection.ToggleImage(image4);
        selection.ToggleImage(image2);
        selection.ToggleImage(image0);

        // 边界：选中的某张图片文件被外部删除 → 该张静默跳过，其余仍能拖出
        File.Delete(image2.Model.ImagePath!);
        var partialFiles = dragDrop.BuildDataObjectForImages(dragItems)
            ?.GetData(DataFormats.FileDrop) as string[];
        Check("选中项文件缺失时静默跳过，其余图片仍可拖出", partialFiles?.Length == 2, $"数量={partialFiles?.Length ?? 0}");

        // 边界：选中的项被"单条移除"（Remove 动作，不是 Reset）→ 队列不能丢项、也不能重复。
        // 队列不再按列表序重排，被移除的项仍留在队列里照常拖出（不丢项）；
        // 追加路径逐项 Contains 判重，同一项也不会被追加两次
        //（写错时的现象是"选了 3 张、拖出去落了 5 个文件"）。
        items.Remove(image4);
        var afterRemove = selection.BuildDragItems();
        Check(
            "选中项被单条移除后队列不丢项、不重复",
            afterRemove.Count == 3 && afterRemove.Distinct().Count() == 3,
            $"Count={afterRemove.Count}，去重后={afterRemove.Distinct().Count()}");

        Check("选择变化会通知界面（Changed 事件）", changedCount > 0, $"次数={changedCount}");
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

    /// <summary>
    /// 按原始 HGLOBAL 读取某个剪切板格式的字节 —— 断言"逐字节还原"用。
    /// 必须用 GlobalFlags 确认句柄是全局内存块：GDI 句柄类格式按 HGLOBAL 强读会拿到垃圾。
    /// </summary>
    private static byte[]? ReadRawClipboardFormat(string format)
    {
        try
        {
            uint id = NativeMethods.RegisterClipboardFormat(format);
            if (id == 0 || !NativeMethods.OpenClipboard(IntPtr.Zero))
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
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch
        {
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
