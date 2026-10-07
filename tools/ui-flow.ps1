# 单进程内完成"激活窗口 -> 交互 -> 截图"，避免切换进程时焦点丢失。
# Usage: pwsh -File tools\ui-flow.ps1 -Script "click:2300,695;wheel:2278,850,1200;shot:04.png"
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Script,
    # 默认存到本仓库的 tools\shots（按脚本位置推导，仓库搬到哪台机器都能用）
    # ⚠️ 只放行了 4 个已知文件名（v12-*.png，见 .gitignore）：**别对着没有隔离数据目录的实例截图**，
    #    否则真实剪切板内容会被带进仓库。要给真实实例截图，先设 LIGHTCLIPBOARD_HOME 指向 %TEMP% 下的隔离目录。
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\shots'),
    [string]$ProcessName = 'LightClipboard'
)

Add-Type -AssemblyName System.Drawing
if (-not ('UiFlow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class UiFlow {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800, MOVE = 0x0001;

    public static IntPtr FindWindow(uint pid, string part) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.Length == 0) return true;
            if (part.Length > 0 && t.ToString().IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void HoverTo(int x, int y) { SetCursorPos(x, y); mouse_event(MOVE, 0, 0, 0, UIntPtr.Zero); }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
    public static void WheelIt(int x, int y, int delta) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(WHEEL, 0, 0, delta, UIntPtr.Zero);
    }
    public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
    public static void SendHotkey(byte mod, byte key) { SendCombo(mod, 0, key); }
    public static void SendCombo(byte m1, byte m2, byte key) {
        keybd_event(m1, 0, 0, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 2, UIntPtr.Zero);
        keybd_event(m1, 0, 2, UIntPtr.Zero);
    }
    public static void DragTo(int fromX, int fromY, int toX, int toY) {
        SetCursorPos(fromX, fromY);
        System.Threading.Thread.Sleep(160);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(200);
        int steps = 24;
        for (int i = 1; i <= steps; i++) {
            SetCursorPos(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
            System.Threading.Thread.Sleep(25);
        }
        System.Threading.Thread.Sleep(400);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
    public static bool IsVisible(IntPtr h) { return IsWindowVisible(h); }
    public static bool IsIconicWindow(IntPtr h) { return IsIconic(h); }
    public static void PressVk(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
    }
    public static void TypeAscii(string text) {
        foreach (char c in text) {
            byte vk;
            if (c >= 'a' && c <= 'z') vk = (byte)(0x41 + (c - 'a'));
            else if (c >= 'A' && c <= 'Z') vk = (byte)(0x41 + (c - 'A'));
            else if (c >= '0' && c <= '9') vk = (byte)(0x30 + (c - '0'));
            else if (c == ' ') vk = 0x20;
            else continue;
            PressVk(vk);
        }
    }
}
'@
}

[void][UiFlow]::SetProcessDPIAware()

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "进程未运行: $ProcessName"; exit 2 }
$pid32 = [uint32]$proc.Id
$hwnd = [UiFlow]::FindWindow($pid32, 'LightClipboard')
if ($hwnd -eq [IntPtr]::Zero) { Write-Error '未找到应用窗口'; exit 3 }

$rect = New-Object UiFlow+RECT
[void][UiFlow]::GetWindowRect($hwnd, [ref]$rect)

# 先把应用激活，确保鼠标/滚轮事件送达（非激活窗口收不到 WM_MOUSEWHEEL）
if ([UiFlow]::IsVisible($hwnd)) { [void][UiFlow]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 400 }

$Steps = $Script.Split(';') | Where-Object { $_.Trim().Length -gt 0 }

$winW = 0; $winH = 0
foreach ($step in $Steps) {
    $parts = $step.Split(':')
    $verb = $parts[0]
    $args2 = @()
    if ($parts.Count -gt 1) { $args2 = $parts[1].Split(',') }

    switch ($verb) {
        'click' {
            [UiFlow]::Click([int]$args2[0], [int]$args2[1])
            Start-Sleep -Milliseconds 500
        }
        'hover' {
            [UiFlow]::HoverTo([int]$args2[0], [int]$args2[1])
            Start-Sleep -Milliseconds 500
        }
        'wheel' {
            [UiFlow]::WheelIt([int]$args2[0], [int]$args2[1], [int]$args2[2])
            Start-Sleep -Milliseconds 600
        }
        'sleep' {
            Start-Sleep -Milliseconds ([int]$args2[0])
        }
        'shot' {
            $out = Join-Path $OutDir $args2[0]
            $dir = Split-Path -Parent $out
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
            $script:winW = $w; $script:winH = $h
            $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
            $g.Dispose()
            $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            Write-Output "shot -> $out"
        }
        'foreground' {
            Write-Output "foreground pid=$([UiFlow]::ForegroundPid()) app pid=$pid32"
        }
        'hotkey' {
            # hotkey:17,16,86 = Ctrl+Shift+V ; hotkey:17,0,86 = Ctrl+V
            $m2 = if ($args2.Count -gt 2) { [byte]$args2[1] } else { [byte]0 }
            $key = if ($args2.Count -gt 2) { [byte]$args2[2] } else { [byte]$args2[1] }
            [UiFlow]::SendCombo([byte]$args2[0], $m2, $key)
            Start-Sleep -Milliseconds 800
        }
        'drag' {
            [UiFlow]::DragTo([int]$args2[0], [int]$args2[1], [int]$args2[2], [int]$args2[3])
            Start-Sleep -Milliseconds 800
        }
        'visible' {
            Write-Output "app window visible=$([UiFlow]::IsVisible($hwnd)) foregroundPid=$([UiFlow]::ForegroundPid())"
        }
        'notepadtext' {
            $np = Get-Process -Name notepad -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($np) {
                $txt = [UiFlow]::FindWindow([uint32]$np.Id, '')
                Write-Output "notepad hwnd=$($txt.ToInt64())"
            } else {
                Write-Output 'notepad 未运行'
            }
        }
        'focus' {
            $target = Get-Process -Name $args2[0] -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $target) { Write-Output "进程不存在: $($args2[0])" }
            else {
                $th = [UiFlow]::FindWindow([uint32]$target.Id, '')
                [void][UiFlow]::SetForegroundWindow($th)
                Start-Sleep -Milliseconds 600
                Write-Output "focus $($args2[0]) hwnd=$($th.ToInt64()) -> foregroundPid=$([UiFlow]::ForegroundPid())"
            }
        }
        'clip' {
            $v = Get-Clipboard -Raw
            Write-Output "clipboard=[$v]"
        }
        'target' {
            $f = Join-Path $env:TEMP 'paste-target.txt'
            if (Test-Path $f) { Write-Output "paste-target=[$(Get-Content $f -Raw -Encoding UTF8)]" }
            else { Write-Output 'paste-target 文件不存在' }
        }
        'rect' {
            $r2 = New-Object UiFlow+RECT
            [void][UiFlow]::GetWindowRect($hwnd, [ref]$r2)
            Write-Output "rect L=$($r2.Left) T=$($r2.Top) R=$($r2.Right) B=$($r2.Bottom) visible=$([UiFlow]::IsVisible($hwnd))"
        }
        'type' {
            [UiFlow]::TypeAscii($args2[0])
            Start-Sleep -Milliseconds 700
        }
    }
}

Write-Output "done (window $($rect.Left),$($rect.Top) ${winW}x${winH})"
