using System;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LightClipboard.Services;

/// <summary>
/// 主题应用（WPF-UI / Fluent）。做一层薄封装，避免 ViewModel 直接依赖 UI 库。
/// </summary>
public static class ThemeService
{
    /// <summary>应用主题设置。</summary>
    public static void Apply(AppThemeMode mode)
    {
        try
        {
            ApplicationTheme target = mode switch
            {
                AppThemeMode.Light => ApplicationTheme.Light,
                AppThemeMode.Dark => ApplicationTheme.Dark,
                _ => SystemToApplicationTheme(ApplicationThemeManager.GetSystemTheme()),
            };

            ApplicationThemeManager.Apply(target);
            Log.Info($"已应用主题: {target}");
        }
        catch (Exception ex)
        {
            Log.Warn("应用主题失败", ex);
        }
    }

    /// <summary>让窗口跟随系统主题变化（Mica 背景）。</summary>
    public static void WatchSystemTheme(Window window)
    {
        try
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: true);
        }
        catch (Exception ex)
        {
            Log.Warn("订阅系统主题变化失败", ex);
        }
    }

    private static ApplicationTheme SystemToApplicationTheme(SystemTheme theme) => theme switch
    {
        SystemTheme.Light => ApplicationTheme.Light,
        SystemTheme.Dark => ApplicationTheme.Dark,
        _ => ApplicationTheme.Dark,
    };
}
