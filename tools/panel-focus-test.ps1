# 呼出焦点行为端到端验证脚本（开发期工具，不参与程序运行）
#
# 验证 v1.2.2 的焦点策略：
#   1. 呼出面板时光标**不**落在搜索框（不自动进入输入模式），面板本身仍拿到前台焦点；
#   2. 直接打字不再被送进搜索框（type-to-search 已取消），也不会触发列表的 type-ahead；
#   3. ↑↓ 不依赖子控件焦点也能翻列表；Esc 不依赖子控件焦点也能收起面板；
#   4. Ctrl+F 是进入搜索的纯键盘入口，进去后输入能正常过滤；
#   5. 再次呼出时上次的搜索词已被清空，且光标依旧不在搜索框里
#      （WPF 激活时可能把焦点还给"上次聚焦的元素"，这条专门防这个回归）。
#
# 前提：先 dotnet build（脚本跑的是本仓库 Debug 产物），且当前没有其它 LightClipboard 在运行。
#       脚本自建隔离数据目录（不动真实历史），并用真实按键（keybd_event）驱动 ——
#       发送按键前会确认面板确实是前台窗口，否则直接失败退出，避免把字打进用户正在用的程序。
#
# 用法：
#   pwsh -File tools\panel-focus-test.ps1
[CmdletBinding()]
param(
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-FocusTest",
    [string]$ShotDir = "$env:TEMP\LightClipboard-FocusTest-shots"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

if (-not ('FocusProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FocusProbe {
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
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const byte VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_UP = 0x26, VK_DOWN = 0x28;
    public const uint KEYUP = 0x0002;

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

    /// <summary>发一次真实按键（按下+抬起）。</summary>
    public static void Tap(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
    }

    /// <summary>Ctrl + 字母。</summary>
    public static void CtrlKey(byte vk) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }
}
'@
}

$script:Fail = 0
function Assert-Equal([string]$label, $actual, $expected) {
    if ("$actual" -eq "$expected") {
        Write-Output ("PASS  {0} = {1}" -f $label, $actual)
    } else {
        Write-Output ("FAIL  {0}: 实际 {1} / 期望 {2}" -f $label, $actual, $expected)
        $script:Fail++
    }
}

$CT = [System.Windows.Automation.ControlType]
# 注意：$True 是 PowerShell 只读自动变量，别拿来当条件对象用
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition

# ---------- 启动隔离实例 ----------
if (Get-Process -Name LightClipboard -ErrorAction SilentlyContinue) {
    Write-Error '已有 LightClipboard 在运行（单实例互斥体会互相干扰），请先退出。'
    exit 2
}

if (Test-Path $DataHome) { Remove-Item $DataHome -Recurse -Force }
New-Item -ItemType Directory -Path $DataHome -Force | Out-Null
if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Path $ShotDir -Force | Out-Null }
$env:LIGHTCLIPBOARD_HOME = $DataHome

# 启动时面板即显示（走 ShowPanel 路径），方便直接核对"呼出后"的状态
$proc = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Write-Output "启动演示实例 pid=$($proc.Id)  home=$DataHome"
$script:appPid = [uint32]$proc.Id

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 50 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 400
    $hwnd = [FocusProbe]::FindMain($appPid, 'LightClipboard')
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error '未找到主面板窗口'; exit 3 }
Start-Sleep -Milliseconds 2500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Get-SearchBox {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Edit)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-SearchValue {
    $box = Get-SearchBox
    if (-not $box) { return '<未找到搜索框>' }
    return $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Get-SearchFocused {
    $box = Get-SearchBox
    if (-not $box) { return '<未找到搜索框>' }
    return [bool]$box.Current.HasKeyboardFocus
}

function Get-List {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::List)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Count {
    $list = Get-List
    if (-not $list) { return -1 }
    return $list.FindAll([System.Windows.Automation.TreeScope]::Children, $AnyCond).Count
}

function Wait-Count([int]$expected, [int]$timeoutMs = 4000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ($true) {
        $n = Get-Count
        if ($n -eq $expected -or (Get-Date) -ge $deadline) { return $n }
        Start-Sleep -Milliseconds 250
    }
}

# 默认面板是 470×700，8 张卡片放不下 —— 折叠在视口外的卡片不会被 UIA 暴露（条数会偏少）。
# 计数前把窗口拉高并确认真的生效，否则"7 条"会被误读成功能问题。
function Ensure-TallWindow([int]$minHeight = 950) {
    for ($i = 0; $i -lt 4; $i++) {
        $r = New-Object FocusProbe+RECT
        [void][FocusProbe]::GetWindowRect($hwnd, [ref]$r)
        if (($r.Bottom - $r.Top) -ge $minHeight) { return $true }
        [void][FocusProbe]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 40, 470, 1010, 0x0014)
        Start-Sleep -Milliseconds 600
    }
    return $false
}

# 选中项的"身份"：取卡片里第一个像标题的文本（跳过只有一个字符的图标字形）
function Get-SelectedLabel {
    $list = Get-List
    if (-not $list) { return '<无列表>' }
    $sel = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($sel.Count -eq 0) { return '<无选中>' }
    $textCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)
    $names = @($sel.Item(0).FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) |
        ForEach-Object { $_.Current.Name })
    $title = $names | Where-Object { $_.Length -gt 4 } | Select-Object -First 1
    if (-not $title) { return '<无标题>' }
    if ($title.Length -gt 20) { return $title.Substring(0, 20) }
    return $title
}

# 发按键前必须确认面板在前台，否则字会打进用户正在用的程序
function Assert-KeyTargetReady {
    if ([FocusProbe]::IsForeground($hwnd)) { return $true }
    [void][FocusProbe]::ForceForeground($hwnd)
    Start-Sleep -Milliseconds 600
    if ([FocusProbe]::IsForeground($hwnd)) { return $true }
    Write-Output 'FAIL  面板不在前台，已跳过后续按键（避免把按键打进别的程序）'
    $script:Fail++
    return $false
}

function Save-Shot([string]$file) {
    $r = New-Object FocusProbe+RECT
    [void][FocusProbe]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    $g.Dispose()
    $out = Join-Path $ShotDir $file
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "      截图 -> $out"
}

# ---------- 1. 呼出后面板拿到前台，但搜索框没有光标 ----------
[void][FocusProbe]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 700
Assert-Equal '窗口已拉高（卡片能全部实例化）' (Ensure-TallWindow) 'True'
Assert-Equal '面板是前台窗口' ([FocusProbe]::IsForeground($hwnd)) 'True'
Assert-Equal '呼出后搜索框没有键盘焦点' (Get-SearchFocused) 'False'
Assert-Equal '呼出后搜索框为空' (Get-SearchValue) ''
Assert-Equal '初始列表条数' (Wait-Count 8) 8
Save-Shot '01-summoned-no-caret.png'

# ---------- 2. 先建立选中项（启动时演示数据还没入库，选中项本来就是空的：↓ 落到第一张） ----------
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap([FocusProbe]::VK_DOWN)
    Start-Sleep -Milliseconds 500
}
$sel = Get-SelectedLabel
Write-Output "      当前选中项: $sel"
Assert-Equal '↓ 能在没有子控件焦点时选中卡片' ([bool]($sel -ne '<无选中>')) 'True'

# ---------- 3. 直接打字：不进搜索框，也不改列表与选中 ----------
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap(0x50)          # 'p'（演示数据里有以 p 开头的卡片标题，能暴露 ListBox 的 type-ahead）
    Start-Sleep -Milliseconds 700
}
Assert-Equal '直接打字后搜索框仍为空' (Get-SearchValue) ''
Assert-Equal '直接打字后列表条数不变' (Get-Count) 8
Assert-Equal '直接打字后选中项不变（没有触发列表 type-ahead）' (Get-SelectedLabel) $sel

# ---------- 4. ↑↓ 继续可用 ----------
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap([FocusProbe]::VK_DOWN)
    Start-Sleep -Milliseconds 500
}
$afterDown = Get-SelectedLabel
Assert-Equal '↓ 后选中项发生变化' ([bool]($afterDown -ne $sel)) 'True'
Write-Output "      ↓ 后选中项: $afterDown"
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap([FocusProbe]::VK_UP)
    Start-Sleep -Milliseconds 500
}
Assert-Equal '↑ 后选中项回到原处' (Get-SelectedLabel) $sel

# ---------- 5. Ctrl+F 进入搜索并过滤 ----------
if (Assert-KeyTargetReady) {
    [FocusProbe]::CtrlKey(0x46)      # Ctrl+F
    Start-Sleep -Milliseconds 800
}
Assert-Equal 'Ctrl+F 后搜索框拿到键盘焦点' (Get-SearchFocused) 'True'
Save-Shot '02-ctrl-f-focused.png'
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap(0x5A)          # 'z'，演示数据里没有任何匹配
    Start-Sleep -Milliseconds 800
}
Assert-Equal 'Ctrl+F 后输入 z 生效（搜索框内容）' (Get-SearchValue) 'z'
Assert-Equal '搜索过滤生效（0 条）' (Wait-Count 0) 0

# ---------- 5. Esc 收起面板（焦点在搜索框里也照收） ----------
if (Assert-KeyTargetReady) {
    [FocusProbe]::Tap([FocusProbe]::VK_ESCAPE)
    Start-Sleep -Milliseconds 800
}
Assert-Equal 'Esc 后面板隐藏' ([FocusProbe]::IsWindowVisible($hwnd)) 'False'

# ---------- 6. 再次呼出：搜索词清空、光标仍不在搜索框 ----------
$second = Start-Process -FilePath $Exe -ArgumentList '--hidden' -PassThru
$second.WaitForExit(5000) | Out-Null     # 二次启动只负责通知已有实例显示面板
$shown = $false
for ($i = 0; $i -lt 20 -and -not $shown; $i++) {
    Start-Sleep -Milliseconds 400
    $shown = [FocusProbe]::IsWindowVisible($hwnd)
}
Assert-Equal '二次启动唤出面板' $shown 'True'
[void][FocusProbe]::ForceForeground($hwnd)
Start-Sleep -Milliseconds 900
Assert-Equal '再次呼出后搜索框为空（上次关键词已清）' (Get-SearchValue) ''
Assert-Equal '再次呼出后列表恢复 8 条' (Wait-Count 8) 8
Assert-Equal '再次呼出后搜索框仍没有键盘焦点' (Get-SearchFocused) 'False'
Save-Shot '03-resummoned-clean.png'

# ---------- 收尾 ----------
$proc.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

Write-Output ''
if ($script:Fail -eq 0) { Write-Output 'PANEL-FOCUS CHECK: PASS' } else { Write-Output "PANEL-FOCUS CHECK: FAIL ($script:Fail 项)" }
exit $(if ($script:Fail -eq 0) { 0 } else { 1 })
