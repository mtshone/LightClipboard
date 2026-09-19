using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightClipboard.ViewModels;

namespace LightClipboard.Services;

/// <summary>
/// 演示数据（--demo）：写入若干真实感较强的记录，
/// 便于人工核对卡片布局、Emoji 渲染、图片缩略图与拖拽行为。
/// </summary>
public static class DemoData
{
    public static async Task SeedAsync(AppHost host)
    {
        try
        {
            Log.Info("写入演示数据…");

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Text,
                Text = "LightClipboard · 用 C# + WPF 写的剪切板增强工具 ✨\n支持文本、Emoji、截图与图片拖拽。",
                SourceApp = "LightClipboard",
            });

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Text,
                Text = "https://learn.microsoft.com/dotnet/desktop/wpf/overview/?view=netdesktop-9.0",
                SourceApp = "msedge",
            });

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Text,
                Text = "private void ImageCard_MouseMove(object sender, MouseEventArgs e)\n{\n    if (e.LeftButton != MouseButtonState.Pressed) return;\n    var data = new DataObject();\n    data.SetFileDropList(files);\n    data.SetImage(bitmap);\n    DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);\n}",
                SourceApp = "devenv",
            });

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Text,
                Text = "剪切板是系统全局共享资源，其他程序写入时读取会抛出 COMException(CLIPBRD_E_CANT_OPEN)，因此需要指数退避重试策略。",
                SourceApp = "WINWORD",
            });

            // 两张“截图”风格图片，验证缩略图与拖拽
            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Image,
                Image = RenderShowcaseImage(1280, 720, "Win+Shift+S 截图示例", new[] { "#2563EB", "#7C3AED", "#DB2777" }),
                PreserveAlpha = false,
                SourceApp = "SnippingTool",
            });

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Image,
                Image = RenderShowcaseImage(960, 540, "图片可拖拽到桌面 / Word / 微信", new[] { "#0EA5E9", "#10B981", "#F59E0B" }),
                PreserveAlpha = false,
                SourceApp = "chrome",
            });

            // 文件条目：使用程序目录下真实存在的文件
            var files = Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();

            if (files.Count == 0)
            {
                files = new List<string> { AppPaths.DatabasePath };
            }

            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Files,
                FilePaths = files,
                SourceApp = "explorer",
            });

            // 最后写入 Emoji 文本，使其排在最前（便于直观核对渲染效果）
            await IngestAsync(host, new ParsedClipboardContent
            {
                Type = Models.ClipboardItemType.Text,
                Text = "今日待办：写代码 💻 / 喝咖啡 ☕ / 遛狗 🐕 / 看电影 🎬 / 保持好心情 ❤️🌈✨",
                SourceApp = "记事本",
            });

            Log.Info("演示数据写入完成");
        }
        catch (Exception ex)
        {
            Log.Warn("写入演示数据失败", ex);
        }
    }

    private static async Task IngestAsync(AppHost host, ParsedClipboardContent content)
    {
        try
        {
            await host.Main.IngestAsync(content).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("演示数据入库失败", ex);
        }
    }

    /// <summary>用 WPF 绘图生成一张“截图”风格的位图（不依赖 System.Drawing）。</summary>
    private static BitmapSource RenderShowcaseImage(int width, int height, string caption, string[] colors)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var stops = new GradientStopCollection();
            for (int i = 0; i < colors.Length; i++)
            {
                stops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), i / (double)(colors.Length - 1)));
            }

            var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));

            // 半透明的“窗口”装饰，让缩略图看起来像截图
            var panel = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            dc.DrawRoundedRectangle(panel, null, new Rect(width * 0.08, height * 0.16, width * 0.84, height * 0.62), 16, 16);

            var lineBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            for (int i = 0; i < 5; i++)
            {
                double w = width * (0.62 - i * 0.08);
                dc.DrawRoundedRectangle(
                    lineBrush,
                    null,
                    new Rect(width * 0.14, height * (0.28 + i * 0.09), w, height * 0.026),
                    6,
                    6);
            }

            var text = new FormattedText(
                caption,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                height * 0.062,
                Brushes.White,
                1.25);

            dc.DrawText(text, new Point(width * 0.09, height * 0.82));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
