# 重拍三张界面截图（v1.2.0：新的顶部导航 + 设置页「隐藏延迟」）。
# 用 UI Automation 按名字选中胶囊，再按主窗口矩形截图；只截窗口本身，不含桌面。
# 用法：pwsh -NoProfile -File tools\shoot-pages.ps1
[CmdletBinding()]
param(
    # 默认存到本仓库的 tools\shots（按脚本位置推导，仓库搬到哪台机器都能用）
    # ⚠️ 只放行了 4 个已知文件名（v12-*.png，见 .gitignore）：**别对着没有隔离数据目录的实例截图**，
    #    否则真实剪切板内容会被带进仓库。要拍真实实例，先设 LIGHTCLIPBOARD_HOME 指向 %TEMP% 下的隔离目录。
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\shots'),
    [string]$ProcessName = 'LightClipboard'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

if (-not ('ShotPages' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ShotPages {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>按“类名以 HwndWrapper 开头 + 标题精确匹配”找主面板窗口，避开 #32770 对话框。</summary>
    public static IntPtr FindMain(uint pid, string title) {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h,l)=>{
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            if (c.ToString().StartsWith("HwndWrapper") && t.ToString() == title) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }

    public static bool ForceForeground(IntPtr hwnd) {
        if (SetForegroundWindow(hwnd)) return true;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint cur = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != cur) AttachThreadInput(cur, fgThread, true);
        if (targetThread != 0 && targetThread != cur && targetThread != fgThread) AttachThreadInput(cur, targetThread, true);
        bool ok; try { ok = SetForegroundWindow(hwnd); } finally {
            if (targetThread != 0 && targetThread != cur && targetThread != fgThread) AttachThreadInput(cur, targetThread, false);
            if (fgThread != 0 && fgThread != cur) AttachThreadInput(cur, fgThread, false); }
        return ok; }
}
'@
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "进程未运行: $ProcessName"; exit 2 }
$hwnd = [ShotPages]::FindMain([uint32]$proc.Id, 'LightClipboard')

# 面板可能是隐藏状态（程序常驻托盘），EnumWindows 找不到它 —— 用"二次启动"通知已有实例显示面板。
# 这一步做成自动的，免得每次重拍都要先手动唤出。
if ($hwnd -eq [IntPtr]::Zero) {
    $exe = Get-ChildItem -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin') -Recurse -Filter 'LightClipboard.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { Write-Error '面板不可见，且找不到 LightClipboard.exe 来唤起它'; exit 3 }
    Write-Output "面板处于隐藏状态，正在唤起（$($exe.FullName)）…"
    Start-Process -FilePath $exe.FullName -ArgumentList '--hidden' | Out-Null
    for ($i = 0; $i -lt 20 -and $hwnd -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        $hwnd = [ShotPages]::FindMain([uint32]$proc.Id, 'LightClipboard')
    }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error '未找到主面板窗口'; exit 3 }

# 窗口可能被 SW_HIDE 之类的手段藏在原生层（此时 EnumWindows 找得到、屏幕上却什么都没有，
# 截出来会是一张全黑图）。强制显示并置前，必要时用二次启动唤醒。
if (-not [ShotPages]::IsWindowVisible($hwnd)) {
    Write-Output '面板在原生层是隐藏的，先 ShowWindow(SW_SHOW) 唤出…'
    [void][ShotPages]::ShowWindow($hwnd, 5)   # SW_SHOW
    Start-Sleep -Milliseconds 600
    $exe = Get-ChildItem -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin') -Recurse -Filter 'LightClipboard.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($exe) { Start-Process -FilePath $exe.FullName -ArgumentList '--hidden' | Out-Null }
    Start-Sleep -Seconds 2
}

[void][ShotPages]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 900

if (-not [ShotPages]::IsWindowVisible($hwnd)) { Write-Error '面板仍不可见，无法截图'; exit 3 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$chipCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::RadioButton)

function Select-Chip([string]$name) {
    $el = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $chipCond) |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $el) { Write-Error "未找到胶囊: $name"; exit 4 }
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1100
}

$rect = New-Object ShotPages+RECT
function Save-Shot([string]$file) {
    [void][ShotPages]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    $g.Dispose()
    $out = Join-Path $OutDir $file
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("shot -> {0}   ({1}x{2} @ {3},{4})" -f $out, $w, $h, $rect.Left, $rect.Top)
}

Select-Chip '全部';      Save-Shot 'v12-history.png'
Select-Chip '😀 Emoji';  Save-Shot 'v12-emoji.png'
Select-Chip '设置';      Save-Shot 'v12-settings.png'
# 收尾：切回历史页，避免下次打开面板停在设置页
Select-Chip '全部'
Write-Output '完成'
Write-Output '提示：窄窗口那张（v12-narrow.png）需要手动把面板缩到 380×420 后另截，脚本不代劳。'
