using System;
using System.IO;

namespace LightClipboard.Services;

/// <summary>
/// 应用的本地数据目录布局。默认位于 %LocalAppData%\LightClipboard，
/// 可通过环境变量 LIGHTCLIPBOARD_HOME 重定向（用于演示/自测隔离）。
/// </summary>
public static class AppPaths
{
    private const string HomeOverrideVariable = "LIGHTCLIPBOARD_HOME";

    public static string Root { get; } = ResolveRoot();

    public static string DatabasePath => Path.Combine(Root, "clipboard.db");

    public static string ImagesDirectory => Path.Combine(Root, "Images");

    public static string ExportsDirectory => Path.Combine(Root, "Exports");

    public static string LogsDirectory => Path.Combine(Root, "Logs");

    public static string SettingsPath => Path.Combine(Root, "settings.json");

    public static string SelfTestReportPath => Path.Combine(LogsDirectory, "selftest.txt");

    private static string ResolveRoot()
    {
        string? custom = Environment.GetEnvironmentVariable(HomeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch (Exception)
            {
                // 非法路径时退回默认目录
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightClipboard");
    }

    /// <summary>创建全部所需目录。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
