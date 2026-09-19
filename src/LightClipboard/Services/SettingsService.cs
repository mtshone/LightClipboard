using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightClipboard.Services;

/// <summary>
/// 设置的读写（JSON 单文件）。任何读写失败都不会影响程序运行。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(AppPaths.SettingsPath))
                {
                    string json = File.ReadAllText(AppPaths.SettingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                    if (loaded != null)
                    {
                        Current = Normalize(loaded);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取设置失败，使用默认设置", ex);
                Current = new AppSettings();
            }
        }
    }

    public void Save()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = Current;
        }

        try
        {
            AppPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(snapshot, Options);
            string tmp = AppPaths.SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存设置失败", ex);
        }
    }

    /// <summary>修改设置并持久化。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Current = Normalize(Current);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>隐藏缓冲时长的允许区间（毫秒）。</summary>
    public const int MinDeferredHideDelayMs = 50;

    public const int MaxDeferredHideDelayMs = 1000;

    /// <summary>隐藏缓冲时长的默认值（毫秒）。</summary>
    public const int DefaultDeferredHideDelayMs = 700;

    private static AppSettings Normalize(AppSettings s)
    {
        s.MaxItems = Math.Clamp(s.MaxItems, 20, 5000);
        s.MaxTextLength = Math.Clamp(s.MaxTextLength, 1000, 2_000_000);
        s.ThumbnailWidth = Math.Clamp(s.ThumbnailWidth, 96, 1024);
        s.DeferredHideDelayMs = Math.Clamp(s.DeferredHideDelayMs, MinDeferredHideDelayMs, MaxDeferredHideDelayMs);
        s.WindowWidth = s.WindowWidth <= 0 ? 0 : Math.Clamp(s.WindowWidth, 380, 2000);
        s.WindowHeight = s.WindowHeight <= 0 ? 0 : Math.Clamp(s.WindowHeight, 420, 2000);
        return s;
    }
}
