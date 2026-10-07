# 「输出只借一次剪切板」端到端验证脚本（开发期工具，不参与程序运行）
#
# 验证 v1.2.4 的行为（点卡片输出不再让"用过一次"的历史条目占住系统剪切板）：
#   1. 点列表里**第二条**卡片 → 目标程序确实收到该条内容（输出真的发生）；
#   2. 粘贴送达后系统剪切板被还原成"输出前的内容"（正常情形就是**列表顶部那条**）——
#      也就是随后按 Ctrl+V 得到的是列表顶部的卡片，而不是刚被输出的这条；
#   3. 列表顺序不因输出而变（第二条仍在原位）；
#   4. 反向对照：把「自动粘贴」关掉（只复制路径）后点卡片，内容就该留在剪切板里等用户自己 Ctrl+V。
#
# 前提：先 dotnet build（脚本跑的是本仓库 Debug 产物），且当前没有其它 LightClipboard 在运行
#       （单实例互斥体会互相干扰）。脚本自建隔离数据目录，不动真实历史。
#
# 用法：
#   pwsh -File tools\restore-clipboard-test.ps1 [-SkipReverse]
#
# 说明：卡片的点击是**真实鼠标点击**（坐标取自 UI Automation 的卡片矩形中心），
#       剪贴板内容用 Get-Clipboard / Set-Clipboard 读写（与 Ctrl+V 看到的是同一份 CF_UNICODETEXT）。

[CmdletBinding()]
param(
    # 隔离数据目录（脚本会先删掉重建）
    [string]$DataHome = (Join-Path $env:TEMP 'LightClipboard-RestoreTest'),
    # 默认跑本仓库的 Debug 产物
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    # 跳过反向对照（只复制路径）
    [switch]$SkipReverse
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

if (-not ('RestoreProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RestoreProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_ESCAPE = 0x1B;
    public const uint KEYUP = 0x0002, LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    /// <summary>按"类名以 HwndWrapper 开头 + 标题精确匹配"找主面板窗口，避开 #32770 对话框。</summary>
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
        if (IsForeground(hwnd)) return true;
        if (SetForegroundWindow(hwnd)) return true;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint tg = GetWindowThreadProcessId(hwnd, out _);
        uint cur = GetCurrentThreadId();
        if (fg != 0 && fg != cur) AttachThreadInput(cur, fg, true);
        if (tg != 0 && tg != cur && tg != fg) AttachThreadInput(cur, tg, true);
        bool ok; try { ok = SetForegroundWindow(hwnd); } finally {
            if (tg != 0 && tg != cur && tg != fg) AttachThreadInput(cur, tg, false);
            if (fg != 0 && fg != cur) AttachThreadInput(cur, fg, false); }
        return ok; }

    public static bool IsForeground(IntPtr hwnd) { return GetForegroundWindow() == hwnd; }

    /// <summary>发一次真实组合键（按下 → 抬起）。</summary>
    public static void SendCombo(byte m1, byte m2, byte key) {
        keybd_event(m1, 0, 0, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, KEYUP, UIntPtr.Zero);
        keybd_event(m1, 0, KEYUP, UIntPtr.Zero);
    }

    public static void Tap(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
    }

    /// <summary>真实鼠标左键单击（物理像素）。</summary>
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}
'@
}

[void][RestoreProbe]::SetProcessDPIAware()

$script:Fail = 0
function Assert-Equal([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") {
        Write-Output ("PASS  {0} = {1}" -f $label, $actual)
    } else {
        Write-Output ("FAIL  {0}: 实际 {1} / 期望 {2}" -f $label, $actual, $expected)
        $script:Fail++
    }
}

function Assert-True([string]$label, $actual) {
    if ($actual) {
        Write-Output ("PASS  {0}" -f $label)
    } else {
        Write-Output ("FAIL  {0}" -f $label)
        $script:Fail++
    }
}

$CT = [System.Windows.Automation.ControlType]
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition

# ---------- 前置检查 ----------
if (-not (Test-Path $Exe)) { Write-Error "找不到产物: $Exe（先 dotnet build）"; exit 2 }
if (Get-Process -Name LightClipboard -ErrorAction SilentlyContinue) {
    Write-Error '已有 LightClipboard 在运行（单实例互斥体会互相干扰），请先退出。'
    exit 2
}

if (Test-Path $DataHome) { Remove-Item $DataHome -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $DataHome 'Logs') -Force | Out-Null
$env:LIGHTCLIPBOARD_HOME = $DataHome

$topText = 'E2E-顶部那条（输出前剪切板里的内容）'
$secondText = 'E2E-第二条（被点击输出的那条）'
$thirdText = 'E2E-第三条（更旧）'

$targetScript = Join-Path $PSScriptRoot 'paste-target.ps1'
$targetOut = Join-Path $env:TEMP 'paste-target.txt'

function Start-App([bool]$autoPaste) {
    if ($autoPaste) {
        $settings = Join-Path $DataHome 'settings.json'
        if (Test-Path $settings) { Remove-Item $settings -Force }
    } else {
        $json = Join-Path $DataHome 'settings.json'
        $obj = if (Test-Path $json) { Get-Content $json -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
        $obj | Add-Member -NotePropertyName AutoPaste -NotePropertyValue $false -Force
        $obj | ConvertTo-Json | Set-Content -Path $json -Encoding UTF8
    }

    $started = Start-Process -FilePath $Exe -PassThru
    $script:appPid = [uint32]$started.Id

    $handle = [IntPtr]::Zero
    for ($i = 0; $i -lt 50 -and $handle -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        $handle = [RestoreProbe]::FindMain($appPid, 'LightClipboard')
    }

    if ($handle -eq [IntPtr]::Zero) { Write-Error '未找到主面板窗口'; exit 3 }
    Start-Sleep -Milliseconds 1500
    return $started
}

function Get-AppLog {
    $file = Get-ChildItem (Join-Path $DataHome 'Logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return '' }
    return (Get-Content $file.FullName -Raw -Encoding UTF8)
}

# 活动热键：日志里记了实际注册成功的组合（可能因被占用而降级）
function Get-ActiveHotkey {
    $log = Get-AppLog
    $match = [regex]::Match($log, '全局热键注册成功: (\S+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return 'Ctrl+Shift+V'
}

function Send-AppHotkey {
    switch ((Get-ActiveHotkey)) {
        'Ctrl+Alt+V' { [RestoreProbe]::SendCombo([RestoreProbe]::VK_CONTROL, [RestoreProbe]::VK_MENU, 0x56) }
        'Ctrl+Shift+F12' { [RestoreProbe]::SendCombo([RestoreProbe]::VK_CONTROL, [RestoreProbe]::VK_SHIFT, 0x7B) }
        default { [RestoreProbe]::SendCombo([RestoreProbe]::VK_CONTROL, [RestoreProbe]::VK_SHIFT, 0x56) }
    }
}

function Show-Panel([int]$timeoutMs = 6000) {
    for ($i = 0; $i -lt 3; $i++) {
        Send-AppHotkey
        $deadline = (Get-Date).AddMilliseconds($timeoutMs / 3)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
            if ([RestoreProbe]::IsWindowVisible($hwnd) -and [RestoreProbe]::IsForeground($hwnd)) { return $true }
        }
    }
    return $false
}

$script:root = $null

function Get-Cards {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::List)
    $list = $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $list) { return @() }
    return @($list.FindAll([System.Windows.Automation.TreeScope]::Children, $AnyCond))
}

# 卡片标题 = 卡片里第一个"像正文"的文本（跳过只有一个字符的图标字形与时间副标题）
function Get-CardTitles {
    $textCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)
    $titles = @()
    foreach ($card in Get-Cards) {
        $names = @($card.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) |
            ForEach-Object { $_.Current.Name })
        $titles += ($names | Where-Object { $_.Length -gt 4 } | Select-Object -First 1)
    }
    return $titles
}

# 面板默认高度放不下所有卡片时，视口外的卡片不会被 UIA 暴露 —— 拉高窗口再读
function Ensure-TallWindow([int]$minHeight = 900) {
    for ($i = 0; $i -lt 4; $i++) {
        $r = New-Object RestoreProbe+RECT
        [void][RestoreProbe]::GetWindowRect($hwnd, [ref]$r)
        if (($r.Bottom - $r.Top) -ge $minHeight) { return $true }
        [void][RestoreProbe]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 40, 470, 960, 0x0014)
        Start-Sleep -Milliseconds 600
    }
    return $false
}

function Clear-Clipboard {
    # 用一个空格占位，避免把上一次测试的内容留在剪切板里（也避免"空剪切板"这种边界干扰快照）
    Set-Clipboard -Value ' '
}

# 把某个窗口抢到前台并确认（抢前台会被系统驳回，AttachThreadInput 兜底后多试几次）
function Wait-Foreground([IntPtr]$handle, [int]$timeoutMs = 6000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ([RestoreProbe]::IsForeground($handle)) { return $true }
        [void][RestoreProbe]::ForceForeground($handle)
        Start-Sleep -Milliseconds 400
    }
    return [RestoreProbe]::IsForeground($handle)
}

# ---------- 启动隔离实例 + 造历史 ----------
Write-Output "== 启动隔离实例（数据目录 $DataHome）"
$proc = Start-App $true
$appPid = $script:appPid
$hwnd = [RestoreProbe]::FindMain($appPid, 'LightClipboard')
$script:root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Output "    pid=$appPid hwnd=0x$($hwnd.ToInt64().ToString('X')) 活动热键=$(Get-ActiveHotkey)"

# 依次复制三条：最后复制的"顶部那条"会成为列表首条，同时也是剪切板当前内容
Clear-Clipboard
Start-Sleep -Milliseconds 400
foreach ($text in @($thirdText, $secondText, $topText)) {
    Set-Clipboard -Value $text
    Start-Sleep -Milliseconds 700
}

[void](Ensure-TallWindow)
Start-Sleep -Milliseconds 600
$before = Get-CardTitles

Assert-Equal '前置：列表共 3 张卡片' $before.Count 3
Assert-Equal '前置：列表首条 = 最后复制的那条（顶部）' $before[0] $topText
Assert-Equal '前置：第二条 = 即将被点击输出的那条' $before[1] $secondText
Assert-Equal '前置：剪切板里是顶部那条' (Get-Clipboard -Raw) $topText

# ---------- 启动粘贴目标并让它在前台 ----------
Write-Output ''
Write-Output '== 启动粘贴目标'
# 句柄文件是"上一轮遗留"的重灾区：必须先删掉，否则会读到上次那个已经销毁的窗口句柄，
# 后面的"目标是否在前台"永远为假（真机第一次跑就踩到了）
$handleFile = Join-Path $env:TEMP 'paste-target-handle.txt'
if (Test-Path $handleFile) { Remove-Item $handleFile -Force }

$targetProc = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', $targetScript) -PassThru
$targetHandle = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $targetHandle -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 300
    if (Test-Path $handleFile) {
        $raw = (Get-Content $handleFile -Raw).Trim()
        if ($raw) { $targetHandle = [IntPtr][int64]$raw }
    }
}

if ($targetHandle -eq [IntPtr]::Zero) { Write-Error '粘贴目标窗口没有起来'; exit 4 }

# 先让面板收起，免得它（Topmost）跟粘贴目标抢前台
if ([RestoreProbe]::IsWindowVisible($hwnd)) {
    [void][RestoreProbe]::ForceForeground($hwnd)
    Start-Sleep -Milliseconds 300
    [RestoreProbe]::Tap([RestoreProbe]::VK_ESCAPE)
    Start-Sleep -Milliseconds 600
}

Assert-True '前置：粘贴目标在前台' (Wait-Foreground $targetHandle)

# ---------- 呼出面板并点击第二条 ----------
Write-Output ''
Write-Output '== 呼出面板 → 点击第二条卡片'
Assert-True '呼出后面板在前台' (Show-Panel)

[void](Ensure-TallWindow)
$cards = Get-Cards
if ($cards.Count -lt 2) { Write-Error "卡片不足 2 张（实际 $($cards.Count)）"; exit 5 }

$rect = $cards[1].Current.BoundingRectangle
$clickX = [int]($rect.Left + $rect.Width / 2)
$clickY = [int]($rect.Top + $rect.Height / 2)
Write-Output "    点击第二条卡片中心 ($clickX, $clickY)：$(($before[1]))"
[RestoreProbe]::Click($clickX, $clickY)

# 输出 → 切回目标 → 130ms 后发 Ctrl+V → 再 300ms 后还原剪切板
Start-Sleep -Milliseconds 1800

$logNow = Get-AppLog
$targetGot = if (Test-Path $targetOut) { (Get-Content $targetOut -Raw).Trim() } else { '' }
$clipNow = Get-Clipboard -Raw

Assert-Equal '输出确实发生：粘贴目标收到了第二条' $targetGot $secondText
Assert-Equal '核心：粘贴送达后剪切板已还原为输出前的内容（按 Ctrl+V 得到列表顶部那条）' $clipNow $topText
Assert-True '剪切板里不是刚被输出的那条' ($clipNow -ne $secondText)
Assert-Equal '输出后面板已隐藏' ([RestoreProbe]::IsWindowVisible($hwnd)) 'False'

Assert-True '日志：存下了输出前的剪切板快照' ($logNow -match '已存下输出前的剪切板快照')
Assert-True '日志：粘贴送达后安排了还原' ($logNow -match '粘贴已送达，将在 300 ms 后把剪切板还原')
Assert-True '日志：确实执行了还原' ($logNow -match '剪切板已还原为输出前的内容')
Assert-True '日志：没有还原失败记录' ($logNow -notmatch '剪切板还原失败')

# ---------- 列表顺序不受输出影响 ----------
Write-Output ''
Write-Output '== 再次呼出：列表顺序应与输出前逐字一致'
Assert-True '再次呼出面板' (Show-Panel)
[void](Ensure-TallWindow)
Start-Sleep -Milliseconds 600
$after = Get-CardTitles
Assert-Equal '输出后列表顺序与输出前一致' ($after -join '|') ($before -join '|')

# ---------- 反向对照：只复制路径不还原 ----------
if (-not $SkipReverse) {
    Write-Output ''
    Write-Output '== 反向对照：关掉「自动粘贴」（只复制路径）后点卡片'

    [void][RestoreProbe]::Tap([RestoreProbe]::VK_ESCAPE)
    Start-Sleep -Milliseconds 600
    if (-not $proc.HasExited) { Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800

    $proc = Start-App $false
    $appPid = $script:appPid
    $hwnd = [RestoreProbe]::FindMain($appPid, 'LightClipboard')
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    Start-Sleep -Milliseconds 1200

    [void][RestoreProbe]::ForceForeground($targetHandle)
    Start-Sleep -Milliseconds 400
    Assert-True '反向对照：呼出面板' (Show-Panel)
    [void](Ensure-TallWindow)

    $cards = Get-Cards
    if ($cards.Count -ge 2) {
        $logBefore = (Get-AppLog).Length
        $rect = $cards[1].Current.BoundingRectangle
        [RestoreProbe]::Click([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
        Start-Sleep -Milliseconds 1800

        Assert-Equal '反向对照：只复制路径把内容留在剪切板里' (Get-Clipboard -Raw) $secondText

        $log = Get-AppLog
        $delta = if ($log.Length -gt $logBefore) { $log.Substring($logBefore) } else { '' }
        Assert-True '反向对照：这条路径没有发生还原' ($delta -notmatch '剪切板已还原')
    } else {
        Assert-True '反向对照：卡片不足 2 张' $false
    }
}

# ---------- 收尾 ----------
foreach ($id in @($appPid, $targetProc.Id)) {
    if ($id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
}
Start-Sleep -Milliseconds 500

Write-Output ''
if ($script:Fail -eq 0) {
    Write-Output 'RESTORE-CLIPBOARD CHECK: PASS'
} else {
    Write-Output "RESTORE-CLIPBOARD CHECK: FAIL ($script:Fail 项)"
}
exit $(if ($script:Fail -eq 0) { 0 } else { 1 })
