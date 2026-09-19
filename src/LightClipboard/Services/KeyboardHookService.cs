using System;
using System.Runtime.InteropServices;
using System.Windows;
using LightClipboard.Interop;

namespace LightClipboard.Services;

/// <summary>
/// Win+V 拦截服务（低级键盘钩子 WH_KEYBOARD_LL）。
///
/// 工作原理：
///   1. 全局挂载 WH_KEYBOARD_LL，在系统（explorer 的原生“剪切板历史”）之前看到按键；
///   2. 检测到“Win 键按住 + V 键按下”时，先注入一个无功能的哑键 VK_NONAME (0xFC)，
///      重置 Shell 的 Win 键状态机，避免松开 Win 时弹出开始菜单；
///   3. 通过 Dispatcher 异步派发“唤起面板”，随后立即 return (IntPtr)1 截断 V 键，
///      系统原生剪切板面板与目标窗口都收不到这次按键。
///
/// 安全约束（务必保持）：
///   - 回调必须“快进快出”：耗时超过 LowLevelHooksTimeout（默认 300ms）会被系统强制卸载钩子；
///   - 委托必须保存在字段里（防 GC 回收导致 CallbackOnCollectedDelegate 崩溃）；
///   - 回调内不允许抛异常逃逸到原生栈。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    /// <summary>钩子回调委托的强引用（GC 看不到原生侧的引用，必须由托管侧持有）。</summary>
    private readonly NativeMethods.HookProc _hookProc;

    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>正在注入哑键：注入的输入会再次进入本钩子，用它做重入保护。</summary>
    private bool _injecting;

    /// <summary>本次 Win+V 已处理：屏蔽长按 V 产生的自动重复，避免面板反复开关。</summary>
    private bool _winVHandled;

    private bool _disposed;

    public KeyboardHookService() => _hookProc = HookCallback;

    /// <summary>拦截到一次 Win+V（已切到 UI 线程派发）。</summary>
    public event EventHandler? WinVPressed;

    /// <summary>钩子是否处于挂载状态。</summary>
    public bool IsRunning => _hookId != IntPtr.Zero;

    /// <summary>挂载钩子；返回是否成功（失败原因写入日志与 <see cref="LastError"/>）。</summary>
    public bool Start()
    {
        if (_disposed || IsRunning)
        {
            return IsRunning;
        }

        _winVHandled = false;

        // 钩子回调就在本进程里，hMod 传主模块句柄即可（单文件发布同样适用）
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookId == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            Log.Error($"[KeyboardHookService] 安装低级键盘钩子失败，Win32 错误码: {LastError}");
            return false;
        }

        LastError = 0;
        Log.Info("[KeyboardHookService] 已挂载低级键盘钩子 (WH_KEYBOARD_LL)，开始拦截 Win+V");
        return true;
    }

    /// <summary>卸载钩子；此时 Win+V 退回系统原生行为。</summary>
    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _winVHandled = false;
        Log.Info("[KeyboardHookService] 已卸载低级键盘钩子，Win+V 交还系统");
    }

    /// <summary>最近一次安装失败的 Win32 错误码（0 表示正常）。</summary>
    public int LastError { get; private set; }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && !_injecting)
            {
                int message = wParam.ToInt32();
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    if (data.vkCode == NativeMethods.VK_V && !_winVHandled && IsWinKeyDown())
                    {
                        _winVHandled = true;

                        // 1) 注入哑键，打破 Shell 对“单按 Win 键”的判定（否则松手会弹开始菜单）
                        SendDummyKey();

                        // 2) 异步派发 UI 唤起，绝不在钩子回调里同步做耗时操作
                        RaiseWinVPressed();

                        // 3) 截断本次 V 键：原生剪切板面板与目标窗口都收不到
                        return (IntPtr)1;
                    }
                }
                else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    // V 或 Win 抬起后解除“已处理”标记，下一次 Win+V 仍然生效
                    if (data.vkCode is NativeMethods.VK_V or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN)
                    {
                        _winVHandled = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 回调里抛异常会直接穿到原生栈导致进程崩溃，这里全部吞掉并记日志
            Log.Error("[KeyboardHookService] 钩子回调异常", ex);
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>左 Win 或右 Win 是否处于按下状态。</summary>
    private static bool IsWinKeyDown()
    {
        return (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_LWIN) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_RWIN) & 0x8000) != 0;
    }

    /// <summary>
    /// 注入无害哑键 VK_NONAME (0xFC)，让系统认为 Win 键参与了组合键，
    /// 从而在松开 Win 时不弹出开始菜单。
    /// </summary>
    private void SendDummyKey()
    {
        _injecting = true;
        try
        {
            var inputs = new NativeMethods.INPUT[2];

            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = NativeMethods.VK_NONAME,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            };

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = NativeMethods.VK_NONAME,
                    wScan = 0,
                    dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            };

            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != inputs.Length)
            {
                Log.Warn($"[KeyboardHookService] 哑键注入不完整（{sent}/{inputs.Length}），错误码 {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[KeyboardHookService] 哑键注入失败", ex);
        }
        finally
        {
            _injecting = false;
        }
    }

    /// <summary>切到 UI 线程触发 <see cref="WinVPressed"/>。</summary>
    private void RaiseWinVPressed()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Log.Info("[KeyboardHookService] 拦截到 Win+V，唤起面板");
                WinVPressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("[KeyboardHookService] 处理 Win+V 事件失败", ex);
            }
        }));
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
