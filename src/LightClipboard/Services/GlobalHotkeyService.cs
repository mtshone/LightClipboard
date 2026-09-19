using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LightClipboard.Interop;

namespace LightClipboard.Services;

/// <summary>一个热键定义。</summary>
public sealed record HotkeyDefinition(uint Modifiers, uint VirtualKey, string Display)
{
    public static HotkeyDefinition CtrlShiftV { get; } = new(
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
        NativeMethods.VK_V,
        "Ctrl+Shift+V");

    public static HotkeyDefinition CtrlAltV { get; } = new(
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
        NativeMethods.VK_V,
        "Ctrl+Alt+V");

    public static HotkeyDefinition CtrlShiftF12 { get; } = new(
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
        0x7B,
        "Ctrl+Shift+F12");
}

/// <summary>
/// 全局热键（RegisterHotKey）。若首选组合被其他程序占用，会依次降级尝试备选组合。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0xA11C;

    private static readonly HotkeyDefinition[] Fallbacks =
    {
        HotkeyDefinition.CtrlShiftV,
        HotkeyDefinition.CtrlAltV,
        HotkeyDefinition.CtrlShiftF12,
    };

    private IntPtr _handle = IntPtr.Zero;
    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>热键被按下（UI 线程）。</summary>
    public event EventHandler? Pressed;

    /// <summary>当前实际生效的组合键描述；未注册成功时为 null。</summary>
    public string? ActiveHotkey { get; private set; }

    public bool IsRegistered => _registered;

    /// <summary>尝试注册热键，返回实际生效的组合键描述。</summary>
    public string? Register(IntPtr handle)
    {
        if (_handle == handle && _registered)
        {
            return ActiveHotkey;
        }

        Unregister();

        _handle = handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source == null)
        {
            Log.Error("获取 HwndSource 失败，全局热键不可用");
            _handle = IntPtr.Zero;
            return null;
        }

        _source.AddHook(HwndHook);

        foreach (var definition in Fallbacks)
        {
            if (NativeMethods.RegisterHotKey(handle, HotkeyId, definition.Modifiers, definition.VirtualKey))
            {
                _registered = true;
                ActiveHotkey = definition.Display;
                Log.Info($"全局热键注册成功: {definition.Display}");
                return ActiveHotkey;
            }

            int err = Marshal.GetLastWin32Error();
            Log.Warn($"全局热键 {definition.Display} 注册失败（Win32 错误码 {err}），尝试下一个组合");
        }

        Log.Error("所有候选全局热键均注册失败，只能通过托盘图标唤起面板");
        return null;
    }

    public void Unregister()
    {
        if (_handle != IntPtr.Zero && _registered)
        {
            NativeMethods.UnregisterHotKey(_handle, HotkeyId);
        }

        _source?.RemoveHook(HwndHook);
        _source = null;
        _handle = IntPtr.Zero;
        _registered = false;
        ActiveHotkey = null;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt32() != HotkeyId)
        {
            return IntPtr.Zero;
        }

        handled = true;
        try
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("处理全局热键事件失败", ex);
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
        Unregister();
    }
}
