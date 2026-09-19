using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LightClipboard.Services;

/// <summary>
/// 开机自启（写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LightClipboard";

    /// <summary>当前可执行文件路径（单文件发布时为 exe 本身）。</summary>
    public static string ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return path;
            }

            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName ?? string.Empty;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            string? value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Log.Warn("读取开机自启配置失败", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                return false;
            }

            if (enabled)
            {
                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\" --startup", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }

            Log.Info($"开机自启已{(enabled ? "启用" : "关闭")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("写入开机自启配置失败", ex);
            return false;
        }
    }
}
