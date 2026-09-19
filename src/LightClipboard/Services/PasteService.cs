using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LightClipboard.Interop;

namespace LightClipboard.Services;

/// <summary>
/// 粘贴服务：把焦点切回目标窗口并模拟 Ctrl+V。
/// 全部依赖 STA / UI 线程（SendInput 与 SetForegroundWindow 的要求）。
/// </summary>
public static class PasteService
{
    private static readonly DispatcherTimer FocusTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    static PasteService()
    {
        FocusTimer.Tick += (_, _) =>
        {
            FocusTimer.Stop();
            SendPasteKeystroke();
        };
    }

    /// <summary>获取当前前台窗口句柄（跳过属于本进程的窗口）。</summary>
    public static IntPtr GetExternalForegroundWindow(IntPtr selfHandle)
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 取根窗口，避免拿到子控件句柄
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        if (selfHandle != IntPtr.Zero && hwnd == selfHandle)
        {
            return IntPtr.Zero;
        }

        return hwnd;
    }

    /// <summary>把窗口切到前台（尽力而为）。</summary>
    public static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            if (NativeMethods.SetForegroundWindow(hwnd))
            {
                return true;
            }

            // SetForegroundWindow 受前台锁定限制时会失败，用 AttachThreadInput 绕过
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            uint currentThread = NativeMethods.GetCurrentThreadId();
            if (targetThread == 0 || targetThread == currentThread)
            {
                return false;
            }

            bool attached = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            try
            {
                return NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("激活目标窗口失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 强制把窗口切到前台，返回最终是否真的拿到了前台窗口。
    ///
    /// 场景：由 Win+V 低级键盘钩子唤起面板时，本进程并不是“最后接收输入的进程”，
    /// Windows 的前台锁定（Foreground Lock）会直接驳回 SetForegroundWindow ——
    /// 面板浮起来了，却收不到键盘输入。做法分三步：
    ///   1) 常规 SetForegroundWindow；
    ///   2) 把当前线程挂到“当前前台窗口所在线程”的输入队列上（AttachThreadInput），
    ///      共享输入状态后再次尝试，这是绕开前台锁定的标准做法；
    ///   3) 仍失败则 BringWindowToTop 兜底。
    /// 注意：若前台窗口属于以管理员权限运行的进程，UIPI 会阻止挂接，此时无法抢焦点
    /// （与“自动粘贴到管理员窗口”是同一限制，见 README 已知限制）。
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            if (NativeMethods.SetForegroundWindow(hwnd))
            {
                return true;
            }

            uint currentThread = NativeMethods.GetCurrentThreadId();
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);

            bool attachedForeground = false;
            bool attachedTarget = false;

            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            }

            try
            {
                if (NativeMethods.SetForegroundWindow(hwnd))
                {
                    return true;
                }

                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);

                return NativeMethods.SetForegroundWindow(hwnd)
                    || NativeMethods.GetForegroundWindow() == hwnd;
            }
            finally
            {
                if (attachedTarget)
                {
                    NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                }

                if (attachedForeground)
                {
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("强制切换前台失败", ex);
            return false;
        }
    }

    /// <summary>延时一小段时间后发送 Ctrl+V（等待前台窗口真正获得焦点）。</summary>
    public static void SchedulePaste(TimeSpan delay)
    {
        FocusTimer.Stop();
        FocusTimer.Interval = delay;
        FocusTimer.Start();
    }

    /// <summary>立即发送 Ctrl+V。</summary>
    public static bool SendPasteKeystroke()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            var inputs = new[]
            {
                CreateKeyInput(NativeMethods.VK_CONTROL, keyUp: false),
                CreateKeyInput(NativeMethods.VK_V, keyUp: false),
                CreateKeyInput(NativeMethods.VK_V, keyUp: true),
                CreateKeyInput(NativeMethods.VK_CONTROL, keyUp: true),
            };

            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            Log.Info($"已发送 Ctrl+V（SendInput {sent}/{inputs.Length}，当前前台窗口 0x{foreground.ToInt64():X}，INPUT 结构大小 {Marshal.SizeOf<NativeMethods.INPUT>()}）");

            if (sent != inputs.Length)
            {
                Log.Warn($"SendInput 仅发送 {sent}/{inputs.Length} 个事件，错误码 {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("模拟 Ctrl+V 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 完整流程：激活目标窗口 → 延时 → 模拟 Ctrl+V。调用方应已隐藏自身窗口。
    /// </summary>
    public static void PasteIntoWindow(IntPtr target)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            Log.Warn($"自动粘贴被跳过：目标窗口无效 (0x{target.ToInt64():X})");
            return;
        }

        bool activated = ActivateWindow(target);
        if (!activated)
        {
            Log.Warn($"未能激活目标窗口 0x{target.ToInt64():X}，仍尝试发送按键");
        }

        Log.Info($"准备粘贴到 0x{target.ToInt64():X}（激活={(activated ? "成功" : "失败")}）");
        SchedulePaste(TimeSpan.FromMilliseconds(activated ? 130 : 60));
    }

    private static NativeMethods.INPUT CreateKeyInput(uint virtualKey, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                wScan = 0,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
