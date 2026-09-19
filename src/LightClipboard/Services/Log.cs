using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LightClipboard.Services;

/// <summary>
/// 极简文件日志（线程安全）。剪切板程序长期驻留后台，需要可追溯的运行记录。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            AppPaths.EnsureCreated();
            Cleanup();
        }
        catch
        {
            // 日志系统本身不允许影响主流程
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? ex)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null)
        {
            line += Environment.NewLine + ex;
        }

        Trace.WriteLine(line);

        try
        {
            lock (Gate)
            {
                File.AppendAllText(CurrentLogFile(), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 忽略日志写入失败
        }
    }

    private static string CurrentLogFile()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        return Path.Combine(AppPaths.LogsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    private static void Cleanup()
    {
        var dir = new DirectoryInfo(AppPaths.LogsDirectory);
        if (!dir.Exists)
        {
            return;
        }

        foreach (var file in dir.GetFiles("app-*.log"))
        {
            if (file.LastWriteTime < DateTime.Now.AddDays(-14))
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }
}
