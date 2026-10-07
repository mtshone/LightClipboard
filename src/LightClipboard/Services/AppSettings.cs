using System;

namespace LightClipboard.Services;

/// <summary>外观主题。</summary>
public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>应用设置（持久化为 settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>历史记录上限（不含收藏项）。</summary>
    public int MaxItems { get; set; } = 300;

    /// <summary>单条文本最大保存字符数，超出部分截断。</summary>
    public int MaxTextLength { get; set; } = 100_000;

    /// <summary>是否启用全局热键。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// 是否接管 Windows 原生 Win+V（剪切板历史）：拦截后按 Win+V 唤出本程序面板。
    /// </summary>
    public bool EnableWinVHook { get; set; } = true;

    /// <summary>点击卡片后是否自动粘贴到之前的焦点窗口。</summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>
    /// 面板失去焦点时自动隐藏。
    /// </summary>
    public bool HideOnDeactivate { get; set; } = true;

    /// <summary>
    /// 「始终置顶」：面板失焦后把自己**无焦点**地抬回置顶层最上方，防止被**同样置顶**的窗口
    /// （全屏浏览器、播放器、其它置顶工具）压住 —— 面板本身已是 Topmost，普通窗口压不住它。
    /// 只在 <see cref="HideOnDeactivate"/> 关闭（面板要常驻）时有意义，设置页里这一项也从属于它。
    /// </summary>
    public bool KeepOnTopWhenUnfocused { get; set; }

    /// <summary>
    /// 失焦后的隐藏缓冲时长（毫秒）。失焦时若鼠标键仍按着（很可能正在从别的窗口往面板里拖东西），
    /// 会等这么久再检查一次，期间只要面板收到 DragEnter/DragOver 就取消隐藏。
    /// 详见 resources/04-实现细节与坑点.md 第 6 条。
    /// </summary>
    public int DeferredHideDelayMs { get; set; } = 700;

    /// <summary>开机自动启动。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>主题。</summary>
    public AppThemeMode Theme { get; set; } = AppThemeMode.Dark;

    /// <summary>缩略图解码宽度（像素）。</summary>
    public int ThumbnailWidth { get; set; } = 320;

    /// <summary>复制图片到剪切板时是否同时附带文件路径（部分软件需要）。</summary>
    public bool CopyImageWithFilePath { get; set; }

    /// <summary>监听是否处于暂停状态（不写入新历史）。</summary>
    public bool MonitorPaused { get; set; }

    /// <summary>面板宽度（DIP），0 表示使用默认值。</summary>
    public int WindowWidth { get; set; }

    /// <summary>面板高度（DIP），0 表示使用默认值。</summary>
    public int WindowHeight { get; set; }
}
