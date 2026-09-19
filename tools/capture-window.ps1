# Captures a screenshot of a running LightClipboard window.
# Usage: pwsh -File tools\capture-window.ps1 -ProcessName LightClipboard -OutFile shot.png [-Margin 0]
[CmdletBinding()]
param(
    [string]$ProcessName = 'LightClipboard',
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$Margin = 0,
    [switch]$PrintWindow
)

Add-Type -AssemblyName System.Drawing

if (-not ('WinCap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    // 找到进程的可见顶层窗口（不依赖 Process.MainWindowHandle：
    // WPF 的 ShowInTaskbar=False 窗口有 owner，会被 MainWindowHandle 忽略）
    public static IntPtr FindVisibleWindow(uint pid, string titlePart) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            string title = t.ToString();
            if (title.Length == 0) return true;
            if (titlePart.Length > 0 && title.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}

[void][WinCap]::SetProcessDPIAware()

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Error "没有找到进程: $ProcessName"
    exit 2
}

$hwnd = [WinCap]::FindVisibleWindow([uint32]$proc.Id, 'LightClipboard')
if ($hwnd -eq [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle }
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "进程 $ProcessName 没有可见窗口（可能被隐藏了）"
    exit 2
}
[void][WinCap]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$rect = New-Object WinCap+RECT
if (-not [WinCap]::GetWindowRect($hwnd, [ref]$rect)) { Write-Error 'GetWindowRect 失败'; exit 3 }

$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { Write-Error "窗口尺寸非法: ${w}x${h}"; exit 4 }

$x = $rect.Left - $Margin
$y = $rect.Top - $Margin
$cw = $w + 2 * $Margin
$ch = $h + 2 * $Margin

$bmp = New-Object System.Drawing.Bitmap($cw, $ch, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)

if ($PrintWindow) {
    $hdc = $g.GetHdc()
    [void][WinCap]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
} else {
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($cw, $ch)), [System.Drawing.CopyPixelOperation]::SourceCopy)
}

$g.Dispose()
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "captured hwnd=$hwnd rect=$($rect.Left),$($rect.Top) size=${w}x${h} -> $OutFile"
