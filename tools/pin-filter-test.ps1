# 收藏分区端到端验证脚本（开发期工具，不参与程序运行）
#
# 验证「收藏项只在 ⭐ 里出现」这条规则：用 UI Automation 逐项核对
#   全部 / 文本 / 图片 / 文件 / ⭐ 五个视图的卡片数，以及搜索是否遵守同一条规则。
#
# 为什么要有这个脚本：收藏分区只是一行筛选规则（MainViewModel.PassesFilter），
# 一旦有人给新筛选条件漏掉 !IsPinned，界面不会报错、只会"悄悄多出几条"，
# 靠肉眼翻列表很容易漏看，这里用 UIA 直接把条数断言掉。
#
# 前提：先 dotnet build（脚本跑的是本仓库 Debug 产物），且当前没有其它 LightClipboard 在运行
#       （单实例互斥体会互相干扰）。脚本自建隔离数据目录并写演示数据，不动真实历史。
#
# 用法：
#   pwsh -File tools\pin-filter-test.ps1
#   pwsh -File tools\pin-filter-test.ps1 -ShotDir D:\tmp\shots     # 另外留存核对截图
#
# 断言失败时退出码为 1，可直接接进 CI/发布前检查。
[CmdletBinding()]
param(
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-PinCheck",
    [string]$ShotDir = "$env:TEMP\LightClipboard-PinCheck-shots"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

if (-not ('PinFilterProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PinFilterProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
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

    /// <summary>面板是 Topmost 且失焦即隐藏，必须先确实拿到前台，否则 UIA 读到的是隐藏态的树。</summary>
    public static bool ForceForeground(IntPtr hwnd) {
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

$proc = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Write-Output "启动演示实例 pid=$($proc.Id)  home=$DataHome"

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 50 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 400
    $hwnd = [PinFilterProbe]::FindMain([uint32]$proc.Id, 'LightClipboard')
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error '未找到主面板窗口'; exit 3 }

[void][PinFilterProbe]::ShowWindow($hwnd, 5)     # SW_SHOW
[void][PinFilterProbe]::ForceForeground($hwnd)
# 拉高窗口：ListBox 开了虚拟化，没实例化的卡片不会出现在 UIA 树里（条数会偏少）
[void][PinFilterProbe]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 40, 470, 1010, 0x0014)  # NOZORDER|NOACTIVATE
Start-Sleep -Milliseconds 2500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

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

# UIA 只能看到"已经实例化并参与布局"的卡片：刚切换筛选 / 刚收藏完时，
# 高度最高的「全部」列表偶尔会少读到一张（下一次布局才补齐）。
# 因此计数一律轮询到期望值或超时再断言，别把"布局还没跑完"误判成筛选规则写错了。
function Wait-Count([int]$expected, [int]$timeoutMs = 4000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ($true) {
        $n = Get-Count
        if ($n -eq $expected -or (Get-Date) -ge $deadline) { return $n }
        Start-Sleep -Milliseconds 250
    }
}

# 面板可能被程序自己重新摆位，计数前确认窗口真的够高（否则尾部的卡片不会实例化）
function Ensure-TallWindow([int]$minHeight = 950) {
    for ($i = 0; $i -lt 4; $i++) {
        $r = New-Object PinFilterProbe+RECT
        [void][PinFilterProbe]::GetWindowRect($hwnd, [ref]$r)
        if (($r.Bottom - $r.Top) -ge $minHeight) { return $true }
        [void][PinFilterProbe]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 40, 470, 1010, 0x0014)
        Start-Sleep -Milliseconds 600
    }
    return $false
}

function Get-Titles {
    $list = Get-List
    if (-not $list) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)
    $out = @()
    foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Children, $AnyCond)) {
        $names = @($item.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
            ForEach-Object { $_.Current.Name })
        $out += ($names -join ' ⁞ ')
    }
    return $out
}

function Select-Chip([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::RadioButton)
    $el = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $el) { throw "未找到胶囊: $name" }
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}

function Set-Search([string]$text) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Edit)
    $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $box) { throw '未找到搜索框' }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    Start-Sleep -Milliseconds 900
}

# 卡片上的 📌 按钮没有 AutomationProperties.Name，靠 ToolTip（自动映射到 UIA HelpText）定位
function Invoke-PinOnFirstCard {
    $list = Get-List
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $AnyCond)
    if ($items.Count -eq 0) { throw '列表为空，无法点击收藏' }
    $first = $items.Item(0)
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Button)
    foreach ($b in $first.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        $help = $b.Current.HelpText
        if ($help -like '收藏*' -or $help -like '*取消收藏*') {
            Write-Output ("      点击卡片按钮: 「{0}」 / 卡片标题: {1}" -f $help, (Get-Titles)[0])
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1200
            return
        }
    }
    throw '没找到收藏按钮'
}

function Get-FooterText {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(共 \d+ 条|收藏 \d+ 条)' })
}

function Get-EmptyHint {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        ForEach-Object { $_.Current.Name } |
        Where-Object { $_ -match '还没有|没有匹配|都在收藏|记录都在' })
}

function Save-Shot([string]$file) {
    $r = New-Object PinFilterProbe+RECT
    [void][PinFilterProbe]::GetWindowRect($hwnd, [ref]$r)
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

# 演示数据的固定构成：8 条 = 文本 5 + 图片 2 + 文件 1，且全部未收藏。
# 下面所有条数期望值都由它推出，改 DemoData 时记得同步。
# ---------- 1. 初始：全部 8 条 ----------
Select-Chip '全部'
Assert-Equal '窗口已拉高（卡片能全部实例化）' (Ensure-TallWindow) 'True'
Assert-Equal '初始「全部」条数' (Wait-Count 8) 8
Select-Chip '文本'
Assert-Equal '初始「文本」条数' (Wait-Count 5) 5
Select-Chip '图片'
Assert-Equal '初始「图片」条数' (Wait-Count 2) 2
Select-Chip '文件'
Assert-Equal '初始「文件」条数' (Wait-Count 1) 1
Select-Chip '⭐'
Assert-Equal '初始「⭐」条数' (Wait-Count 0) 0

# ---------- 2. 在「全部」里收藏第一条 → 它必须从「全部」消失 ----------
Set-Search ''
Select-Chip '全部'
Save-Shot '01-all-before-pin.png'
Invoke-PinOnFirstCard
Assert-Equal '收藏后「全部」条数（收藏项已移出）' (Wait-Count 7) 7
Assert-Equal '收藏后「全部」里不再有待办那条' ([bool]((Get-Titles) -match '待办')) 'False'
Save-Shot '02-all-after-pin.png'
Assert-Equal '收藏后页脚文案' ((Get-FooterText) -join ' | ') '共 7 条 · 收藏 1 条'

# ---------- 3. 收藏项不应出现在 文本 / 图片 / 文件 ----------
Select-Chip '文本'
Assert-Equal '收藏后「文本」条数' (Wait-Count 4) 4
Select-Chip '图片'
Assert-Equal '收藏后「图片」条数' (Wait-Count 2) 2
Select-Chip '文件'
Assert-Equal '收藏后「文件」条数' (Wait-Count 1) 1

# ---------- 4. 收藏项应出现在 ⭐，且就是刚收藏的那条 ----------
Select-Chip '⭐'
Assert-Equal '收藏后「⭐」条数' (Wait-Count 1) 1
Save-Shot '03-pinned.png'
$pinnedTitles = Get-Titles
Write-Output ("      ⭐ 中的卡片: {0}" -f ($pinnedTitles -join ' / '))
Assert-Equal '⭐ 里就是刚收藏的那条（内容含“待办”）' ([bool]($pinnedTitles -match '待办')) 'True'

# ---------- 5. 搜索同样严格排除收藏项 ----------
Set-Search '待办'
Assert-Equal '在「⭐」里搜“待办”' (Wait-Count 1) 1
Select-Chip '全部'
Assert-Equal '在「全部」里搜“待办”（收藏项不出现）' (Wait-Count 0) 0
Write-Output ("      空状态: {0}" -f ((Get-EmptyHint) -join ' / '))
Select-Chip '文本'
Assert-Equal '在「文本」里搜“待办”（收藏项不出现）' (Wait-Count 0) 0

# ---------- 6. 非收藏内容照常搜得到（别把规则改成了"搜索什么都不显示"） ----------
Set-Search '共享资源'
Select-Chip '全部'
Assert-Equal '在「全部」里搜“共享资源”（非收藏文本）' (Wait-Count 1) 1
Set-Search ''

# ---------- 7. 在 ⭐ 里取消收藏 → 回到其它视图 ----------
Select-Chip '⭐'
Invoke-PinOnFirstCard
Assert-Equal '取消收藏后「⭐」条数' (Wait-Count 0) 0
Write-Output ("      ⭐ 空状态: {0}" -f ((Get-EmptyHint) -join ' / '))
Save-Shot '04-pinned-empty.png'
Select-Chip '全部'
Assert-Equal '取消收藏后「全部」条数' (Wait-Count 8) 8
Assert-Equal '取消收藏后「全部」里又有待办那条' ([bool]((Get-Titles) -match '待办')) 'True'
Select-Chip '文本'
Assert-Equal '取消收藏后「文本」条数' (Wait-Count 5) 5
Select-Chip '全部'
Set-Search '待办'
Assert-Equal '取消收藏后在「全部」搜“待办”' (Wait-Count 1) 1
Set-Search ''
Select-Chip '全部'
Save-Shot '05-all-restored.png'

# ---------- 收尾 ----------
$proc.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 800
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

Write-Output ''
if ($script:Fail -eq 0) { Write-Output 'PIN-FILTER CHECK: PASS' } else { Write-Output "PIN-FILTER CHECK: FAIL ($script:Fail 项)" }
exit $(if ($script:Fail -eq 0) { 0 } else { 1 })
