using System;
using System.Windows.Interop;
using LightClipboard.Interop;

namespace LightClipboard.Services;

/// <summary>
/// Win32 剪切板全局监听：通过 AddClipboardFormatListener 把剪切板变更
/// 转换为 WM_CLIPBOARDUPDATE 消息，再在窗口消息钩子中派发事件。
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    /// <summary>合并短时间内的多次通知（部分程序写入剪切板会连续触发多条消息）。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(80);

    private HwndSource? _source;
    private IntPtr _handle = IntPtr.Zero;
    private DateTime _lastRaise = DateTime.MinValue;
    private bool _disposed;

    /// <summary>剪切板内容发生变化（已做去抖）。回调运行在 UI 线程。</summary>
    public event EventHandler? ClipboardUpdated;

    /// <summary>暂停/恢复监听（暂停时不会写入新历史）。</summary>
    public bool IsPaused { get; set; }

    public bool IsRunning => _handle != IntPtr.Zero;

    /// <summary>绑定到窗口句柄并开始监听。</summary>
    public bool Start(IntPtr handle)
    {
        if (_handle == handle)
        {
            return true;
        }

        Stop();

        _handle = handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source == null)
        {
            Log.Error("无法获取窗口的 HwndSource，剪切板监听未启动");
            _handle = IntPtr.Zero;
            return false;
        }

        _source.AddHook(HwndHook);

        if (!NativeMethods.AddClipboardFormatListener(handle))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Error($"AddClipboardFormatListener 失败，Win32 错误码 {err}");
            _source.RemoveHook(HwndHook);
            _source = null;
            _handle = IntPtr.Zero;
            return false;
        }

        Log.Info("剪切板监听已启动");
        return true;
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.RemoveClipboardFormatListener(_handle);
        }
        catch (Exception ex)
        {
            Log.Warn("移除剪切板监听失败", ex);
        }

        _source?.RemoveHook(HwndHook);
        _source = null;
        _handle = IntPtr.Zero;
        Log.Info("剪切板监听已停止");
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return IntPtr.Zero;
        }

        // 不使用 handled = true：需要让消息继续传递，避免影响其他监听者。
        if (IsPaused)
        {
            return IntPtr.Zero;
        }

        var now = DateTime.UtcNow;
        if (now - _lastRaise < Debounce)
        {
            return IntPtr.Zero;
        }

        _lastRaise = now;

        try
        {
            ClipboardUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("处理剪切板更新事件时发生异常", ex);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
