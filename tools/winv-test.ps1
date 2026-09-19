# Win+V 拦截端到端验证脚本（开发期工具，不参与程序运行）
#
# 用真实键盘事件（keybd_event）在“粘贴目标窗口”（tools\paste-target.ps1）上按 Win+V，
# 对比按键前后的状态：
#   intercept  开启拦截：面板出现且拿到前台焦点、没有其它新窗口、再按一次收起
#   hotkey     既有 Ctrl+Shift+V 全局热键路径的回归验证（面板同样要可见且获得焦点）
#
# 注意：Windows 只把“非注入”的 Win 组合键交给 Shell 的原生剪切板历史处理，
#       而 keybd_event 属于注入输入，因此在无实体键盘的环境里无法用本脚本复现
#       “原生面板是否弹出”，那一条需要在真实桌面上手动按 Win+V 核对。
#       本脚本能自动核对的，是“拦截后本面板是否正常弹出/获得焦点/开关语义”。
#
# 用法：
#   pwsh -File tools\winv-test.ps1 -Mode intercept
#   pwsh -File tools\winv-test.ps1 -Mode hotkey
[CmdletBinding()]
param(
    [ValidateSet('intercept', 'hotkey')][string]$Mode = 'intercept',
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-WinVTest",
    [int]$WaitMs = 1500,
    [switch]$KeepApp
)

$ErrorActionPreference = 'Stop'
$TargetScript = Join-Path $PSScriptRoot 'paste-target.ps1'
$TargetHandle = Join-Path $env:TEMP 'paste-target-handle.txt'

if (-not ('WinVProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinVProbe {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public const byte VK_LWIN = 0x5B, VK_V = 0x56, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_A = 0x41, VK_DELETE = 0x2E;

    public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }

    public static string Describe(IntPtr h) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        var cls = new StringBuilder(256); GetClassName(h, cls, 256);
        var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
        return pid + "|" + cls + "|" + txt;
    }

    public static string ForegroundInfo() { return Describe(GetForegroundWindow()); }

    public static IntPtr FindWindowByPid(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.Length == 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>强制把窗口切到前台（AttachThreadInput 绕过前台锁定）。</summary>
    public static bool ForceForeground(IntPtr hwnd) {
        if (SetForegroundWindow(hwnd)) return true;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint cur = GetCurrentThreadId();
        bool ok;
        if (fgThread != 0 && fgThread != cur) AttachThreadInput(cur, fgThread, true);
        if (targetThread != 0 && targetThread != cur && targetThread != fgThread) AttachThreadInput(cur, targetThread, true);
        try { ok = SetForegroundWindow(hwnd); } finally {
            if (targetThread != 0 && targetThread != cur && targetThread != fgThread) AttachThreadInput(cur, targetThread, false);
            if (fgThread != 0 && fgThread != cur) AttachThreadInput(cur, fgThread, false);
        }
        return ok;
    }

    public static void SendWinV() {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(VK_V, 0, 2, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(VK_LWIN, 0, 2, UIntPtr.Zero);
    }

    public static void SendCombo(byte mod1, byte mod2, byte key) {
        keybd_event(mod1, 0, 0, UIntPtr.Zero);
        if (mod2 != 0) keybd_event(mod2, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        if (mod2 != 0) keybd_event(mod2, 0, 2, UIntPtr.Zero);
        keybd_event(mod1, 0, 2, UIntPtr.Zero);
    }

    public static void PressKey(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    public static IntPtr[] VisibleTopLevel() {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@
}

[void][WinVProbe]::SetProcessDPIAware()

# ==================================================================
# 公共步骤
# ==================================================================

function Assert-Free-Environment {
    if (Get-Process -Name LightClipboard -ErrorAction SilentlyContinue) {
        Write-Error '已有 LightClipboard 在运行（单实例互斥体会互相干扰），请先退出。'
        exit 2
    }
}

function Start-Target {
    if (Test-Path $TargetHandle) { Remove-Item $TargetHandle -Force }
    $script:target = Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-File', $TargetScript -PassThru
    for ($i = 0; $i -lt 20 -and -not (Test-Path $TargetHandle); $i++) { Start-Sleep -Milliseconds 400 }
    if (-not (Test-Path $TargetHandle)) { Write-Error '粘贴目标窗口未能启动'; exit 4 }
    $script:targetHwnd = [IntPtr][int64](Get-Content $TargetHandle -Raw).Trim()
    Start-Sleep -Milliseconds 400
    return $script:targetHwnd
}

function Focus-Target {
    [void][WinVProbe]::ForceForeground($script:targetHwnd)
    Start-Sleep -Milliseconds 700
}

function Start-App([bool]$hookEnabled) {
    $settings = [ordered]@{ EnableWinVHook = $hookEnabled }
    $settings | ConvertTo-Json | Set-Content -Path (Join-Path $DataHome 'settings.json') -Encoding UTF8
    $proc = Start-Process -FilePath $Exe -ArgumentList '--hidden' -PassThru
    Start-Sleep -Seconds 3
    if ($proc.HasExited) { Write-Error "程序启动后立即退出（退出码 $($proc.ExitCode)）"; exit 3 }
    return $proc
}

function Stop-App($proc) {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600
}

# ==================================================================
# 主体
# ==================================================================

Assert-Free-Environment
Remove-Item -Path $DataHome -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $DataHome -Force | Out-Null
$env:LIGHTCLIPBOARD_HOME = $DataHome

Write-Output "模式: $Mode"

# ---------- intercept / hotkey：界面行为验证 ----------
[void](Start-Target)
$app = Start-App -hookEnabled $true
Focus-Target

$before = @{}
foreach ($h in [WinVProbe]::VisibleTopLevel()) { $before[$h] = [WinVProbe]::Describe($h) }

$fgBefore = [WinVProbe]::ForegroundInfo()
Write-Output "按键前前台: $fgBefore   （程序 pid=$($app.Id)）"

if ($Mode -eq 'hotkey') {
    Write-Output '按下 Ctrl+Shift+V（既有全局热键路径的回归验证）'
    [WinVProbe]::SendCombo([WinVProbe]::VK_CONTROL, [WinVProbe]::VK_SHIFT, [WinVProbe]::VK_V)
} else {
    Write-Output '按下 Win+V'
    [WinVProbe]::SendWinV()
}
Start-Sleep -Milliseconds $WaitMs

$appHwnd = [WinVProbe]::FindWindowByPid([uint32]$app.Id)
$appVisible = ($appHwnd -ne [IntPtr]::Zero)
$appFocused = ([WinVProbe]::ForegroundPid() -eq [uint32]$app.Id)

$new = @()
foreach ($h in [WinVProbe]::VisibleTopLevel()) {
    if (-not $before.ContainsKey($h) -and $h -ne $appHwnd) { $new += [WinVProbe]::Describe($h) }
}

Write-Output "按键后前台: $([WinVProbe]::ForegroundInfo())"
Write-Output "本程序面板可见: $appVisible   面板获得前台焦点: $appFocused"
Write-Output "其它新增顶层窗口（系统剪切板历史 / 开始菜单等）: $($new.Count)"
foreach ($d in $new) {
    $parts = $d.Split('|')
    $pname = (Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue).ProcessName
    Write-Output "  + [$pname] class=$($parts[1]) title=$($parts[2])"
}

$toggleOk = $true
if ($Mode -eq 'intercept' -and $appVisible) {
    [WinVProbe]::SendWinV()
    Start-Sleep -Milliseconds $WaitMs
    $visibleAgain = ([WinVProbe]::FindWindowByPid([uint32]$app.Id) -ne [IntPtr]::Zero)
    Write-Output "再按一次 Win+V 后面板可见: $visibleAgain （期望 False）"
    $toggleOk = -not $visibleAgain
}

$log = Get-ChildItem (Join-Path $DataHome 'Logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    Write-Output '---- 日志（Win+V 相关） ----'
    Get-Content $log.FullName | Select-String -Pattern 'KeyboardHookService|低级键盘钩子' | ForEach-Object { Write-Output "  $_" }
}

$ok = switch ($Mode) {
    'intercept' { $appVisible -and $appFocused -and ($new.Count -eq 0) -and $toggleOk }
    'hotkey' { $appVisible -and $appFocused }
}

Write-Output ''
Write-Output $(if ($ok) { 'RESULT: PASS' } else { 'RESULT: FAIL' })

if (-not $KeepApp) { Stop-App $app }
try { Stop-Process -Id $script:target.Id -Force -ErrorAction SilentlyContinue } catch { }
exit $(if ($ok) { 0 } else { 1 })
