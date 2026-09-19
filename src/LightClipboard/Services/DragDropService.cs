using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>
/// 拖拽服务：封装 OLE Drag &amp; Drop。
/// 关键点是同时提供 FileDrop（文件路径）与 Bitmap / PNG 格式，
/// 这样桌面、资源管理器、Word、Photoshop、微信、QQ 等都能正确接收。
/// </summary>
public sealed class DragDropService
{
    private readonly ImageCacheManager _images;

    public DragDropService(ImageCacheManager images)
    {
        _images = images;
    }

    /// <summary>为一条历史记录构造拖拽数据对象。</summary>
    public DataObject? BuildDataObject(ClipboardItem item)
    {
        var data = new DataObject();

        switch (item.Type)
        {
            case ClipboardItemType.Text:
            {
                if (string.IsNullOrEmpty(item.Text))
                {
                    return null;
                }

                data.SetData(DataFormats.UnicodeText, item.Text);
                data.SetData(DataFormats.Text, item.Text);
                return data;
            }

            case ClipboardItemType.Image:
            {
                var bitmap = _images.LoadFull(item.ImagePath);
                if (bitmap == null)
                {
                    return null;
                }

                // 1) 文件路径格式：桌面 / 资源管理器 / 聊天软件 / 邮件客户端
                string? exportPath = _images.EnsureExportFile(item.ImagePath);
                if (exportPath != null)
                {
                    data.SetFileDropList(new StringCollection { exportPath });
                }

                // 2) 位图格式：Office / 画图 / Photoshop 等
                data.SetImage(bitmap);

                // 3) PNG 流：优先无损且保留透明通道
                try
                {
                    data.SetData("PNG", new MemoryStream(ImageCacheManager.EncodePng(bitmap)));
                }
                catch (Exception ex)
                {
                    Log.Warn("构造拖拽 PNG 数据失败", ex);
                }

                return data;
            }

            case ClipboardItemType.Files:
            {
                if (item.FilePaths.Count == 0)
                {
                    return null;
                }

                var collection = new StringCollection();
                foreach (string path in item.FilePaths)
                {
                    collection.Add(path);
                }

                data.SetFileDropList(collection);

                // 若拖拽的是单张图片文件，同时附加位图格式，方便直接拖进文档
                if (item.FilePaths.Count == 1 && ClipboardDataParser.IsImageFile(item.FilePaths[0]))
                {
                    var bitmap = _images.LoadFull(item.FilePaths[0]);
                    if (bitmap != null)
                    {
                        data.SetImage(bitmap);
                    }
                }

                return data;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 启动一次原生 OLE 拖拽。必须在 UI 线程调用。
    /// </summary>
    /// <returns>拖拽最终效果。</returns>
    public DragDropEffects DoDragDrop(DependencyObject source, ClipboardItem item)
    {
        var data = BuildDataObject(item);
        if (data == null)
        {
            return DragDropEffects.None;
        }

        try
        {
            return DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Warn("拖拽操作失败", ex);
            return DragDropEffects.None;
        }
    }

    /// <summary>解析从外部拖入的数据（返回零个或多个待入库条目）。</summary>
    public void ParseDroppedData(IDataObject data, Action<ParsedClipboardContent> onContent)
    {
        if (data == null)
        {
            return;
        }

        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                onContent(new ParsedClipboardContent
                {
                    Type = ClipboardItemType.Files,
                    FilePaths = new List<string>(files),
                    SourceApp = "拖拽",
                });

                // 单张图片文件额外追加一条图片记录，方便直接粘贴位图
                if (files.Length == 1 && ClipboardDataParser.IsImageFile(files[0]) && File.Exists(files[0]))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(files[0], UriKind.Absolute);
                        bitmap.EndInit();
                        bitmap.Freeze();

                        onContent(new ParsedClipboardContent
                        {
                            Type = ClipboardItemType.Image,
                            Image = bitmap,
                            PreserveAlpha = true,
                            SourceApp = "拖拽",
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取拖入的图片文件失败", ex);
                    }
                }

                return;
            }

            if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG"))
            {
                BitmapSource? bitmap = null;
                bool preserveAlpha = false;

                if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream pngStream)
                {
                    using (pngStream)
                    {
                        pngStream.Position = 0;
                        var frame = BitmapFrame.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        frame.Freeze();
                        bitmap = frame;
                        preserveAlpha = true;
                    }
                }
                else if (data.GetData(DataFormats.Bitmap) is BitmapSource source)
                {
                    bitmap = ClipboardDataParser.MakeOpaque(source);
                }

                if (bitmap != null)
                {
                    onContent(new ParsedClipboardContent
                    {
                        Type = ClipboardItemType.Image,
                        Image = bitmap,
                        PreserveAlpha = preserveAlpha,
                        SourceApp = "拖拽",
                    });
                }

                return;
            }

            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                onContent(new ParsedClipboardContent
                {
                    Type = ClipboardItemType.Text,
                    Text = text,
                    SourceApp = "拖拽",
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("解析拖入内容失败", ex);
        }
    }
}
