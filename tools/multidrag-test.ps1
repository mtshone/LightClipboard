# 多选图片拖拽输出 端到端验证脚本（开发期工具，不参与程序运行）
#
# 验证的行为：
#   Ctrl+左键逐张选图片、Shift+左键按列表顺序选一段（跨文本/文件时自动跳过）、
#   拖动任一已选图片把**全部已选图片**以多个文件的形式一次性拖出，
#   以及"不按修饰键"的单张路径与 v1.2.4 完全一致。
#
# 为什么要单独测：多选是**纯鼠标手势**，自检（--selftest）只能覆盖状态机与数据对象，
# 盖不住"预览事件里怎么分派手势"这一段；而这里恰好有几条不出错、只失灵的坑：
#   1. 在 MouseUp 里读 Keyboard.Modifiers（而不是按下时的快照）
#      → "松手前先松开 Ctrl"会被误判成普通单击：清空多选 + 顺手粘贴一次；
#   2. 修饰键分支里顺手调了 ActivateCommand → 每次 Ctrl+点击都写剪切板、收面板、切窗口；
#   3. 拖拽入参没换成集合版 → 选了 N 张却只拖出 1 张（界面不报错，只是"没生效"）；
#   4. Shift 范围把区间内的文本/文件也算成选中 → 计数虚高、拖出去的文件数不对。
#   5. 多选拖出的**顺序口径**弄反（v1.3.1）：**Ctrl 逐张点选 → 整批按点击顺序输出**
#      （取消后重新点选排到队列末尾）；**Shift 划出来的区间 → 仍按列表顺序**。
#      两条都由本脚本正面覆盖（multi / shift 用例）。
#
# 用例：
#   shift        Ctrl 点一张图片（先验 Ctrl 手势没坏）→ 普通单击同一张建立**纯 Shift 锚点** →
#                Shift 点下方一张文本卡：区间内的文本被跳过，计数只算图片；再 Shift 点上方的文件卡：
#                区间整体替换、计数同步变小（锚点不动）；最后 Shift 拉到末张 → 拖出：
#                **顺序 = 列表顺序**（纯 Shift 与 v1.3.0 一致，与 multi 的点击序形成对照）
#   reject       Ctrl 点文本卡片：已有选择不变 + 出现「多选仅支持图片」提示条
#   modsnapshot  "按住 Ctrl 点下去 → 先松开 Ctrl → 再松开鼠标左键"：仍按**选择**处理
#                （面板不收、不粘贴、剪切板内容原样）
#   clear        普通单击一张卡片：选择归零 + 面板按原行为收起（重新呼出后没有"已选"提示）
#   multi        Ctrl 选中**全部**图片（故意倒序点击）→ 拖动其中一张 → 目标一次收到 N 个 PNG，
#                数量 = 已选张数、顺序 = **点击顺序**（按 PNG 尺寸与卡片标题逐一核对）、
#                文件名 = 导出目录里的文件、日志出现「已构造多图拖拽数据：N 个文件」、
#                面板按现有规则自动收起
#   single       反向对照：不按修饰键拖 1 张图片 → 目标只收到 1 个文件、日志里没有多图记录
#   nodrag       反向对照：按住 Ctrl 拖动 → 不启动拖拽（目标什么都没收到、面板保留）
#
# 断言期望值全部**由界面读出来的卡片标题推导**（图片尺寸写在标题里），不写死张数：
# 演示数据（--demo）里图片有几张、排第几行都不影响本脚本。
#
# 用法：
#   pwsh -File tools\multidrag-test.ps1                 # 全部用例
#   pwsh -File tools\multidrag-test.ps1 -Mode multi     # 只跑一个用例
#
# 前置条件：退出正在使用的 LightClipboard 实例（单实例互斥体会互相干扰）；
#           先 dotnet build（脚本跑的是本仓库 Debug 产物）。
# 脚本自建隔离数据目录（%TEMP%\LightClipboard-MultiDragTest）并以 --demo 启动自己的实例，
# 另外拉起一个"接收方"窗口（WinForms 列表框，声明接受 FileDrop）——
# 它等价于资源管理器 / 聊天软件的接收路径（多文件 FileDrop 的落地方式与资源管理器一致）。
[CmdletBinding()]
param(
    [ValidateSet('all', 'shift', 'reject', 'modsnapshot', 'clear', 'multi', 'single', 'nodrag')][string]$Mode = 'all',
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-MultiDragTest",
    [string]$ShotDir = "$env:TEMP\LightClipboard-MultiDragTest-shots",
    [switch]$KeepApp
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes

if (-not ('MultiDragProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MultiDragProbe {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }

    // 主窗口必须按"标题 = LightClipboard 且类名以 HwndWrapper 开头"来找，
    // 只按标题会抓到确认对话框（#32770），量出来的矩形完全不对（07 篇记录过这个坑）
    public static IntPtr FindMainWindow(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            var c = new StringBuilder(512); GetClassName(h, c, 512);
            if (t.ToString() != title) return true;
            if (!c.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindWindowByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.ToString() != title) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>面板是 Topmost 且失焦即隐藏，UIA 读数前必须确实拿到前台。</summary>
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

    public static void MoveResize(IntPtr h, int x, int y, int cx, int cy) {
        SetWindowPos(h, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    public static void SendCombo(byte m1, byte m2, byte key) {
        keybd_event(m1, 0, 0, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(m1, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Ctrl+单击：按下 Ctrl → 点击 → 松开 Ctrl（按下瞬间的修饰键快照就该读到 Ctrl）。</summary>
    public static void CtrlClick(int x, int y) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(120);
        Click(x, y);
        System.Threading.Thread.Sleep(120);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Shift+单击：同上。</summary>
    public static void ShiftClick(int x, int y) {
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(120);
        Click(x, y);
        System.Threading.Thread.Sleep(120);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// "按住 Ctrl 点下去 → 先松开 Ctrl → 再松开鼠标左键"。
    /// 这是快照设计要防的那个场景：MouseUp 时才读 Keyboard.Modifiers 的话，
    /// 这次点击会被当成普通单击（清空多选 + 复制粘贴 + 收面板）。
    /// </summary>
    public static void CtrlClickReleasingModifierFirst(int x, int y) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);   // 修饰键先松
        System.Threading.Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>按住左键从 A 拖到 B。esc=true 时松手前先按 Esc（OLE 视为取消）。</summary>
    public static void Drag(int fromX, int fromY, int toX, int toY, bool esc) {
        SetCursorPos(fromX, fromY);
        System.Threading.Thread.Sleep(200);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(250);
        for (int i = 1; i <= 20; i++) {
            SetCursorPos(fromX + (toX - fromX) * i / 20, fromY + (toY - fromY) * i / 20);
            System.Threading.Thread.Sleep(35);
        }
        System.Threading.Thread.Sleep(350);
        if (esc) {
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            keybd_event(0x1B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(300);
        }
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>按住 Ctrl 拖动：修饰键按下时**不该**启动拖拽（见方案 §2.1 / 铁律 3）。</summary>
    public static void CtrlDrag(int fromX, int fromY, int toX, int toY) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(120);
        Drag(fromX, fromY, toX, toY, false);
        System.Threading.Thread.Sleep(120);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
'@
}
[void][MultiDragProbe]::SetProcessDPIAware()

$script:Failures = 0
$script:Checks = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $script:Checks++
    if ($Ok) {
        Write-Output "  [PASS] $Name$(if ($Detail) { "  ($Detail)" })"
    } else {
        $script:Failures++
        Write-Output "  [FAIL] $Name$(if ($Detail) { "  ($Detail)" })"
    }
}

$CT = [System.Windows.Automation.ControlType]
$AnyCond = [System.Windows.Automation.Condition]::TrueCondition
$TextCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::Text)

function Get-AppLogPath { Join-Path $DataHome ("Logs\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd')) }

function Get-LogLineCount {
    $p = Get-AppLogPath
    return $(if (Test-Path $p) { @(Get-Content $p).Count } else { 0 })
}

function Get-NewLog([int]$Since) {
    $p = Get-AppLogPath
    if (-not (Test-Path $p)) { return '' }
    return (@(Get-Content $p | Select-Object -Skip $Since) -join "`n")
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 6000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return [bool](& $Condition)
}

# ------------------------------------------------------------------
# 面板与卡片定位（全部走 UIA，不写死坐标：卡片高度随内容变化，图片卡比文本卡矮）
# ------------------------------------------------------------------

$script:Hwnd = [IntPtr]::Zero

# UIA 调用会在目标 UI 线程忙（例如刚入库一张图片、正在编码 PNG）时抛超时，
# 这是环境噪声而不是被测行为出错，所以统一重试几次；真失败时仍然抛出去，不静默吞掉。
function New-UiaRoot([int]$Retries = 4) {
    $last = $null
    for ($i = 0; $i -lt $Retries; $i++) {
        try { return [System.Windows.Automation.AutomationElement]::FromHandle($script:Hwnd) }
        catch { $last = $_; Start-Sleep -Milliseconds 500 }
    }
    throw $last
}

function Get-Root { return New-UiaRoot }

function Get-List {
    $root = New-UiaRoot
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT::List)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Cards {
    $list = Get-List
    if (-not $list) { return @() }
    $out = @()
    $i = 0
    foreach ($it in $list.FindAll([System.Windows.Automation.TreeScope]::Children, $AnyCond)) {
        $r = $it.Current.BoundingRectangle
        $texts = @($it.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond) | ForEach-Object { $_.Current.Name })
        $out += [pscustomobject]@{
            Index  = $i
            Left   = [int]$r.Left
            Top    = [int]$r.Top
            Right  = [int]$r.Right
            Bottom = [int]$r.Bottom
            CX     = [int](($r.Left + $r.Right) / 2)
            CY     = [int](($r.Top + $r.Bottom) / 2)
            Joined = ($texts -join ' ')
        }
        $i++
    }
    return $out
}

# 面板摆到工作区右下角并拉高：ListBox 开了虚拟化，没实例化的卡片不在 UIA 树里。
# 与 tools/dragout-test.ps1 同一套处置（Ensure-TallWindow）。
function Place-Panel {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $h = [Math]::Min(1010, $wa.Height - 24)
    $x = $wa.Right - 470 - 12
    $y = $wa.Bottom - $h - 12
    [MultiDragProbe]::MoveResize($script:Hwnd, $x, $y, 470, $h)
    Start-Sleep -Milliseconds 900
    return $h
}

# 让面板可见并成为前台窗口（"点击别处会收起"这类断言只有在面板确实持有焦点时才有意义）。
# 已经可见就别按热键 —— TogglePanel 在"可见且激活"时会把它收起来。
function Ensure-Panel {
    if (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd)) {
        $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        [void][MultiDragProbe]::SetCursorPos([int]($wa.Left + $wa.Width / 2), [int]($wa.Top + $wa.Height / 2))
        Start-Sleep -Milliseconds 300
        [MultiDragProbe]::SendCombo(17, 16, 86)   # Ctrl+Shift+V
        Start-Sleep -Milliseconds 1500
    }
    [void][MultiDragProbe]::ShowWindow($script:Hwnd, 5)
    [void][MultiDragProbe]::ForceForeground($script:Hwnd)
    Start-Sleep -Milliseconds 400
    [void](Place-Panel)
    return [MultiDragProbe]::IsWindowVisible($script:Hwnd)
}

function Get-FooterTexts {
    return @((Get-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(共 \d+ 条|已选 )' })
}

function Get-SelectionCount {
    $hint = @((Get-FooterTexts) | Where-Object { $_ -match '^已选 (\d+) 张图片' })
    if ($hint.Count -eq 0) { return 0 }
    return [int]([regex]::Match($hint[0], '^已选 (\d+) 张图片').Groups[1].Value)
}

function Wait-SelectionCount([int]$Expected, [int]$TimeoutMs = 4000) {
    [void](Wait-Until { (Get-SelectionCount) -eq $Expected } $TimeoutMs)
    return (Get-SelectionCount)
}

# 图片卡片的标题里写着像素尺寸（"图片 960 × 540"），据此推导期望值 —— 不写死张数与尺寸
function Get-ImageCards([object[]]$Cards) {
    return @($Cards | Where-Object { $_.Joined -match '×' } | Sort-Object Index)
}

function Get-CardSize([object]$Card) {
    $m = [regex]::Match($Card.Joined, '(\d+)\s*×\s*(\d+)')
    return $(if ($m.Success) { "$($m.Groups[1].Value)x$($m.Groups[2].Value)" } else { '' })
}

function Get-JoinedCardPattern([object[]]$Cards) {
    return (($Cards | ForEach-Object { "[$($_.Index)] $($_.Joined)" }) -join "`n")
}

# 把剪切板设成哨兵文本并等它入库 —— 之后所有修饰键点击都不该改写剪切板，用它一眼就能验。
# 注意：入库会把新记录插到列表最前，调用后必须重新调用 Get-Cards 取坐标。
function Set-ClipboardSentinel([string]$Text) {
    Set-Clipboard -Value $Text
    $ok = Wait-Until { @((Get-Cards) | Where-Object { $_.Joined -match [regex]::Escape($Text) }).Count -eq 1 } 6000
    Start-Sleep -Milliseconds 700
    return $ok
}

# 区间内图片卡片的张数（按列表 index 升序区间统计，含两端）
function Get-ImagesInRange([object[]]$Cards, [int]$A, [int]$B) {
    $lo = [Math]::Min($A, $B); $hi = [Math]::Max($A, $B)
    return @($Cards | Where-Object { $_.Index -ge $lo -and $_.Index -le $hi -and $_.Joined -match '×' }).Count
}

# ------------------------------------------------------------------
# 接收方：WinForms 列表框，声明接受 FileDrop，把每次落入的文件路径按顺序追加到文件
# ------------------------------------------------------------------

function Get-Drops {
    if (-not (Test-Path $receiverFile)) { return @() }
    $blocks = @()
    $cur = $null
    foreach ($line in @(Get-Content $receiverFile)) {
        if ($line.Trim() -eq '#DROP') {
            if ($null -ne $cur) { $blocks += , @($cur) }
            $cur = @()
        } elseif ($null -ne $cur -and $line.Trim()) {
            $cur += $line.Trim()
        }
    }
    if ($null -ne $cur) { $blocks += , @($cur) }
    return $blocks
}

function Get-ImageSize([string]$Path) {
    $img = [System.Drawing.Image]::FromFile($Path)
    try { return "{0}x{1}" -f $img.Width, $img.Height } finally { $img.Dispose() }
}

function Save-Shot([string]$File) {
    if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Path $ShotDir -Force | Out-Null }
    $r = New-Object MultiDragProbe+RECT
    [void][MultiDragProbe]::GetWindowRect($script:Hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    $g.Dispose()
    $out = Join-Path $ShotDir $File
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "      截图 -> $out"
}

$receiverScript = Join-Path $DataHome 'multidrag-target.ps1'
$receiverFile = Join-Path $DataHome 'multidrag-target.txt'
$app = $null
$receiver = $null

try {
    # ---------- 前置检查 ----------
    if (Get-Process -Name LightClipboard -ErrorAction SilentlyContinue) {
        Write-Output 'RESULT: FAIL — 已有 LightClipboard 实例在运行，请先退出（单实例互斥体会互相干扰）'
        exit 1
    }
    if (-not (Test-Path $Exe)) {
        Write-Output "RESULT: FAIL — 未找到可执行文件：$Exe（先 dotnet build）"
        exit 1
    }

    # ---------- 隔离数据目录 ----------
    if (Test-Path $DataHome) { Remove-Item $DataHome -Recurse -Force }
    New-Item -ItemType Directory -Path $DataHome -Force | Out-Null
    if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Path $ShotDir -Force | Out-Null }

    # ---------- 接收方 ----------
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    # 接收方放在左上，被测面板在工作区右下：两者绝不重叠 ——
    # 否则"松手点仍在面板内"会被 IsDropInsidePanel 判成内部拖拽，面板不会收起。
    $targetW = [Math]::Min(560, [Math]::Max(320, [int]($wa.Width * 0.4)))
    $targetH = [Math]::Min(340, [Math]::Max(200, [int]($wa.Height * 0.35)))
    @"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
`$out = '$receiverFile'
Set-Content -Path `$out -Value '' -Encoding UTF8
`$form = New-Object System.Windows.Forms.Form
`$form.Text = 'LightClipboard MultiDrag Target'
`$form.StartPosition = 'Manual'
`$form.Location = New-Object System.Drawing.Point(20, 20)
`$form.Size = New-Object System.Drawing.Size($targetW, $targetH)
`$form.TopMost = `$true
`$lb = New-Object System.Windows.Forms.ListBox
`$lb.Dock = 'Fill'
`$lb.AllowDrop = `$true
`$lb.SelectionMode = 'None'
`$lb.Add_DragEnter({ param(`$s, `$e)
        if (`$e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { `$e.Effect = [System.Windows.Forms.DragDropEffects]::Copy }
        else { `$e.Effect = [System.Windows.Forms.DragDropEffects]::None } })
`$lb.Add_DragOver({ param(`$s, `$e)
        if (`$e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { `$e.Effect = [System.Windows.Forms.DragDropEffects]::Copy }
        else { `$e.Effect = [System.Windows.Forms.DragDropEffects]::None } })
`$lb.Add_DragDrop({ param(`$s, `$e)
        if (`$e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) {
            `$e.Effect = [System.Windows.Forms.DragDropEffects]::Copy
            `$paths = [string[]]`$e.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
            `$lines = @('#DROP') + `$paths
            Add-Content -Path `$out -Value `$lines -Encoding UTF8
        } })
`$form.Controls.Add(`$lb)
[void]`$form.Show()
[System.Windows.Forms.Application]::Run(`$form)
"@ | Set-Content -Path $receiverScript -Encoding UTF8

    $receiver = Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-File', $receiverScript -WindowStyle Hidden -PassThru
    # 必须等接收方窗口真的出现**再**启动被测程序：它是个 TopMost 窗口，
    # 晚出现会把面板挤掉焦点，面板（默认「失焦即隐藏」）会自己收起来，后面所有点击全部落空。
    $targetReady = Wait-Until { [MultiDragProbe]::FindWindowByTitle('LightClipboard MultiDrag Target') -ne [IntPtr]::Zero } 25000
    if (-not $targetReady) {
        Write-Output 'RESULT: FAIL — 接收方窗口未就绪'
        exit 1
    }
    Write-Output '接收方窗口已就绪'

    $env:LIGHTCLIPBOARD_HOME = $DataHome
    $app = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 4

    $script:Hwnd = [MultiDragProbe]::FindMainWindow('LightClipboard')
    if ($script:Hwnd -eq [IntPtr]::Zero) {
        Write-Output 'RESULT: FAIL — 未找到面板主窗口'
        exit 1
    }

    $targetHwnd = [MultiDragProbe]::FindWindowByTitle('LightClipboard MultiDrag Target')
    $trect = New-Object MultiDragProbe+RECT
    [void][MultiDragProbe]::GetWindowRect($targetHwnd, [ref]$trect)
    $dropX = [int](($trect.Left + $trect.Right) / 2)
    $dropY = [int](($trect.Top + $trect.Bottom) / 2)
    Write-Output "接收方落点=($dropX,$dropY)"

    [void](Ensure-Panel)
    Check '面板已就绪且为前台窗口' (([MultiDragProbe]::ForegroundPid()) -eq $app.Id)

    # 落点必须在面板外，否则 IsDropInsidePanel 会判成"内部拖拽"，面板不会收起、
    # "拖出成功"这条主线也验不出来（属于本脚本的前提，不成立就没必要往下跑）
    $prect = New-Object MultiDragProbe+RECT
    [void][MultiDragProbe]::GetWindowRect($script:Hwnd, [ref]$prect)
    $outside = $dropX -lt $prect.Left -or $dropX -ge $prect.Right -or $dropY -lt $prect.Top -or $dropY -ge $prect.Bottom
    Check '接收方落点在面板之外（前提）' $outside "面板=($($prect.Left),$($prect.Top))-($($prect.Right),$($prect.Bottom))"

    # 演示数据：文本 5 + 图片 2 + 文件 1
    $seedCards = @(Get-Cards)
    Check '演示数据已装载（8 张卡片）' ($seedCards.Count -eq 8) "实际=$($seedCards.Count)"
    Check '演示数据里有图片卡片可供多选' ((Get-ImageCards $seedCards).Count -ge 2) "图片=$(Get-ImageCards $seedCards | ForEach-Object { Get-CardSize $_ })"

    # ---------- 用例 shift：纯 Shift 范围跨文本 / 文件时只收图片，顺序仍 = 列表顺序 ----------
    if ($Mode -in @('all', 'shift')) {
        Write-Output ''
        Write-Output '== 用例 shift（纯 Shift 范围只选图片 / 锚点不动 / 顺序 = 列表顺序）=='

        [void](Ensure-Panel)
        $seedCards = @(Get-Cards)
        $seedImg = (Get-ImageCards $seedCards)[0]

        # 先验一下 Ctrl 手势本身没被改坏：Ctrl 点击 = 切换单张（不复制、不粘贴、不收面板）
        [MultiDragProbe]::CtrlClick($seedImg.CX, $seedImg.CY)
        Start-Sleep -Milliseconds 500
        Check 'Ctrl 点击图片后计数 = 1' ((Wait-SelectionCount 1) -eq 1) "实际=$(Get-SelectionCount)"

        # v1.3.1 起锚点分两种：**Ctrl 点击**建立的是"点击序"模式（其后 Shift 只做末尾追加），
        # **普通单击**建立的是纯 Shift 模式（其后 Shift 整体替换为区间、按列表顺序）。
        # 本用例下面要验"纯 Shift 不用改"，所以这里用普通单击把锚点重新落到同一张图片上；
        # 普通单击本身仍是原行为（复制 / 自动粘贴 / 收起面板），并清空多选（计数归零）。
        [MultiDragProbe]::Click($seedImg.CX, $seedImg.CY)
        Start-Sleep -Milliseconds 1500
        Check '普通单击按原行为收起面板（锚点建立 = 纯 Shift 模式）' (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd))
        [void](Ensure-Panel)

        $sentinelShift = 'MULTIDRAG-SENTINEL-SHIFT'
        Check '哨兵文本已入库' (Set-ClipboardSentinel $sentinelShift)

        # 入库会插到最前，卡片坐标全部要重新读（锚点是引用，插入不影响它）
        $cards = @(Get-Cards)
        $imgs = Get-ImageCards $cards
        $anchor = $imgs[0]                                   # 列表里最靠前的那张图片 = 上面普通单击的那张
        $texts = @($cards | Where-Object { $_.Joined -notmatch '×' -and $_.Joined -notmatch '个文件' })
        $textBelow = @($texts | Where-Object { $_.Index -gt $anchor.Index } | Sort-Object Index | Select-Object -First 1)[0]
        $nonImageAbove = @($cards | Where-Object { $_.Joined -notmatch '×' -and $_.Index -lt $anchor.Index } | Sort-Object Index)[-1]

        Check '找到了锚点下方/上方的非图片卡片（用例有意义）' ($null -ne $textBelow -and $null -ne $nonImageAbove)

        # 往下 Shift 到一张文本卡：区间跨过文本行，只有图片该被选中
        [MultiDragProbe]::ShiftClick($textBelow.CX, $textBelow.CY)
        Start-Sleep -Milliseconds 500
        $expected = Get-ImagesInRange $cards $anchor.Index $textBelow.Index
        Check "Shift 范围跨非图片只收图片（期望 $expected 张）" ((Wait-SelectionCount $expected) -eq $expected) "实际=$(Get-SelectionCount)"

        # 再往上 Shift 到一张非图片卡：纯 Shift 下区间整体**替换**（锚点不动，计数同步变小）。
        # 若锚点被丢掉，这一下会退化成"只选这一张"，而它本身选不进来 → 计数会掉到 0。
        [MultiDragProbe]::ShiftClick($nonImageAbove.CX, $nonImageAbove.CY)
        Start-Sleep -Milliseconds 500
        $expected2 = Get-ImagesInRange $cards $nonImageAbove.Index $anchor.Index
        Check "再次 Shift 以原锚点调整范围（期望 $expected2 张，锚点没丢）" ((Wait-SelectionCount $expected2) -eq $expected2) "实际=$(Get-SelectionCount)"

        Check '修饰键点击不收起面板' ([MultiDragProbe]::IsWindowVisible($script:Hwnd))
        Check '修饰键点击不碰剪切板（哨兵原样）' (((Get-Clipboard -Raw).Trim()) -eq $sentinelShift) "现在=[$((Get-Clipboard -Raw).Trim())]"
        Save-Shot '01-shift-range.png'

        # ---- v1.3.1 正面对照：纯 Shift 划出来的区间 → 顺序 = 列表顺序 ----
        # Shift 只是"重划区间"，这里不存在点击序：整段区间按 Items 的 index 升序（由上往下）输出。
        # 锚点仍是上面普通单击的那张（列表里最靠前的图片），这一次 Shift 把区间一直拉到末张，
        # 于是整批 = 全部图片，拖出去的文件顺序必须是列表顺序 —— 与 multi 用例的点击顺序形成对照。
        [MultiDragProbe]::ShiftClick($imgs[-1].CX, $imgs[-1].CY)
        Start-Sleep -Milliseconds 500
        Check "Shift 拉回整段区间（期望 $($imgs.Count) 张）" ((Wait-SelectionCount $imgs.Count) -eq $imgs.Count) "实际=$(Get-SelectionCount)"

        $expectedShiftSizes = @($imgs | ForEach-Object { Get-CardSize $_ })   # 列表顺序 = 期望的拖出顺序
        $dropsBefore = @(Get-Drops).Count
        $logLines = Get-LogLineCount
        [MultiDragProbe]::Drag($imgs[-1].CX, $imgs[-1].CY, $dropX, $dropY, $false)
        Start-Sleep -Milliseconds 1800

        $drops = @(Get-Drops)
        $newLog = Get-NewLog $logLines
        $receivedShift = if ($drops.Count -gt $dropsBefore) { $drops[-1] } else { @() }
        $receivedShiftSizes = @($receivedShift | ForEach-Object { Get-ImageSize $_ })

        Check 'Shift 整段拖出：目标收到了一批文件（只落一次）' ($drops.Count -eq $dropsBefore + 1) "落下次数=$($drops.Count)"
        Check "Shift 整段拖出：数量 = 区间内图片数（$($imgs.Count) 个文件）" ($receivedShift.Count -eq $imgs.Count) "实际=$($receivedShift.Count)"
        Check "Shift 整段拖出：日志出现「已构造多图拖拽数据：$($imgs.Count) 个文件」" ($newLog -match "已构造多图拖拽数据：$($imgs.Count) 个文件")
        Check "纯 Shift 区间：顺序 = 列表顺序（$($expectedShiftSizes -join ' → ')）" (($receivedShiftSizes -join ',') -eq ($expectedShiftSizes -join ',')) "实际=$($receivedShiftSizes -join ',')"
        Check 'Shift 整段拖出后面板按现有规则收起' (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd))
        # 拖出走的是 OLE DataObject，不经过系统剪切板：哨兵必须还在（v1.2.4「借一次就还」不受影响）
        Check '拖出整批不碰剪切板（哨兵原样）' (((Get-Clipboard -Raw).Trim()) -eq $sentinelShift) "现在=[$((Get-Clipboard -Raw).Trim())]"
    }

    # ---------- 用例 reject：Ctrl 点非图片被拒绝，且不改动已有选择 ----------
    if ($Mode -in @('all', 'reject')) {
        Write-Output ''
        Write-Output '== 用例 reject（Ctrl 点文本卡片：拒绝 + 提示条）=='

        [void](Ensure-Panel)
        $cards = @(Get-Cards)
        $textCard = @($cards | Where-Object { $_.Joined -notmatch '×' -and $_.Joined -notmatch '个文件' } | Sort-Object Index | Select-Object -First 1)[0]
        $before = Get-SelectionCount
        [MultiDragProbe]::CtrlClick($textCard.CX, $textCard.CY)

        # 提示条只显示 2.4 秒（MainViewModel._toastTimer），这里轮询 UIA 抓它
        $script:toastSeen = $false
        [void](Wait-Until {
                $names = @((Get-Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, $TextCond) |
                    ForEach-Object { $_.Current.Name })
                $script:toastSeen = [bool]($names -match '多选仅支持图片')
                return $script:toastSeen
            } 3000)
        Check '出现提示条「多选仅支持图片」' $script:toastSeen
        Start-Sleep -Milliseconds 300
        Check '拒绝时已有选择不变' ((Get-SelectionCount) -eq $before) "之前=$before 之后=$(Get-SelectionCount)"
        Save-Shot '02-reject-toast.png'
        Start-Sleep -Milliseconds 2500   # 等提示条自己消失，别影响后面的 UIA 断言
    }

    # ---------- 用例 modsnapshot：先松 Ctrl、再松鼠标 = 仍然按选择处理 ----------
    if ($Mode -in @('all', 'modsnapshot')) {
        Write-Output ''
        Write-Output '== 用例 modsnapshot（松手前先松开 Ctrl，仍按选择处理）=='

        [void](Ensure-Panel)

        # 哨兵必须真的躺在剪切板里，否则"没被改写"这条断言是空转的
        $sentinelMod = 'MULTIDRAG-SENTINEL-MODSNAP'
        Check '哨兵文本已入库' (Set-ClipboardSentinel $sentinelMod)

        # 选择状态在用例之间会变，卡片矩形也可能被重新布局，这里重新读一次
        $cards = @(Get-Cards)
        $imgLast = (Get-ImageCards $cards)[-1]
        $before = Get-SelectionCount
        $clipBefore = (Get-Clipboard -Raw).Trim()
        Check '哨兵确实在剪切板里' ($clipBefore -eq $sentinelMod) "现在=[$clipBefore]"
        [MultiDragProbe]::CtrlClickReleasingModifierFirst($imgLast.CX, $imgLast.CY)
        Start-Sleep -Milliseconds 600

        Check '仍被当成选择手势（面板没被收起）' ([MultiDragProbe]::IsWindowVisible($script:Hwnd))
        Check '计数按切换处理（增 1 或减 1）' ([Math]::Abs((Get-SelectionCount) - $before) -eq 1) "之前=$before 之后=$(Get-SelectionCount)"
        Check '剪切板内容原样（没有顺手复制一次）' (((Get-Clipboard -Raw).Trim()) -eq $sentinelMod) "现在=[$((Get-Clipboard -Raw).Trim())]"
    }

    # ---------- 用例 clear：普通单击清空多选 + 面板按原行为收起 ----------
    if ($Mode -in @('all', 'clear')) {
        Write-Output ''
        Write-Output '== 用例 clear（普通单击：选择归零 + 面板收起）=='

        [void](Ensure-Panel)
        Write-Output "      清空前计数=$(Get-SelectionCount)"
        $plainCards = @(Get-Cards)
        $plainCard = @($plainCards | Where-Object { $_.Joined -notmatch '×' -and $_.Joined -notmatch '个文件' } | Sort-Object Index | Select-Object -First 1)[0]
        [MultiDragProbe]::Click($plainCard.CX, $plainCard.CY)
        Start-Sleep -Milliseconds 1500

        Check '普通单击后面板按原行为收起' (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd))
        [void](Ensure-Panel)
        Check '重新呼出后没有「已选」提示（选择已清空）' ((Get-SelectionCount) -eq 0) "实际=$(Get-SelectionCount)"
        Save-Shot '03-after-clear.png'
    }

    # ---------- 用例 multi：Ctrl 选中全部图片 → 拖一张，整批出去 ----------
    if ($Mode -in @('all', 'multi')) {
        Write-Output ''
        Write-Output '== 用例 multi（Ctrl 选中全部图片 → 拖动其中一张，全部拖出）=='

        [void](Ensure-Panel)
        $cards = @(Get-Cards)
        $imgs = Get-ImageCards $cards
        Check '有 2 张以上图片可供多选' ($imgs.Count -ge 2) "张数=$($imgs.Count)"
        # 故意不按列表顺序点击：只要这次选择里有 Ctrl 点击，拖出顺序就必须 = **点击顺序**（v1.3.1）
        $clickOrder = @($imgs | Sort-Object Index -Descending)
        $expectedSizes = @($clickOrder | ForEach-Object { Get-CardSize $_ })   # 点击顺序 = 期望的拖出顺序

        foreach ($c in $clickOrder) {
            [MultiDragProbe]::CtrlClick($c.CX, $c.CY)
            Start-Sleep -Milliseconds 350
        }
        Check "Ctrl 选中全部图片（计数 = $($imgs.Count)）" ((Wait-SelectionCount $imgs.Count) -eq $imgs.Count) "实际=$(Get-SelectionCount)"
        Save-Shot '04-all-images-selected.png'

        $dropsBefore = @(Get-Drops).Count
        $logLines = Get-LogLineCount

        # 从列表里最后一张已选图片开始拖：已选卡片 → 整批
        $dragFrom = $imgs[-1]
        [MultiDragProbe]::Drag($dragFrom.CX, $dragFrom.CY, $dropX, $dropY, $false)
        Start-Sleep -Milliseconds 1800

        $drops = @(Get-Drops)
        $newLog = Get-NewLog $logLines
        $received = if ($drops.Count -gt $dropsBefore) { $drops[-1] } else { @() }
        $receivedSizes = @($received | ForEach-Object { Get-ImageSize $_ })

        Check '目标收到了一批文件（只落一次）' ($drops.Count -eq $dropsBefore + 1) "落下次数=$($drops.Count)"
        Check "数量 = 已选张数（$($imgs.Count) 个文件）" ($received.Count -eq $imgs.Count) "实际=$($received.Count)"
        Check "日志出现「已构造多图拖拽数据：$($imgs.Count) 个文件」" ($newLog -match "已构造多图拖拽数据：$($imgs.Count) 个文件")
        Check '收到的文件全部真实存在' (@($received | Where-Object { Test-Path $_ }).Count -eq $imgs.Count)
        Check '文件名与导出目录一致' (@($received | Where-Object { Test-Path (Join-Path $DataHome "Exports\$(Split-Path $_ -Leaf)") }).Count -eq $imgs.Count)
        Check "顺序 = 点击顺序（$($expectedSizes -join ' → ')）" (($receivedSizes -join ',') -eq ($expectedSizes -join ',')) "实际=$($receivedSizes -join ',')（点击顺序=$($expectedSizes -join ',')，列表顺序=$(@($imgs | ForEach-Object { Get-CardSize $_ }) -join ',')）"
        Check '拖出成功后面板按现有规则收起' (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd))
    }

    # ---------- 用例 single：反向对照，不按修饰键仍只拖一张 ----------
    if ($Mode -in @('all', 'single')) {
        Write-Output ''
        Write-Output '== 用例 single（反向对照：不按修饰键只拖 1 张，单张路径未被改坏）=='

        [void](Ensure-Panel)
        $cards = @(Get-Cards)
        $imgLast = (Get-ImageCards $cards)[-1]
        $expectedSize = Get-CardSize $imgLast
        Check '单张拖拽前没有选择态' ((Get-SelectionCount) -eq 0) "实际=$(Get-SelectionCount)"

        $dropsBefore = @(Get-Drops).Count
        $logLines = Get-LogLineCount
        [MultiDragProbe]::Drag($imgLast.CX, $imgLast.CY, $dropX, $dropY, $false)
        Start-Sleep -Milliseconds 1800

        $drops = @(Get-Drops)
        $newLog = Get-NewLog $logLines
        $received = if ($drops.Count -gt $dropsBefore) { $drops[-1] } else { @() }
        Check '目标只收到 1 个文件' ($received.Count -eq 1) "实际=$($received.Count)"
        Check "收到的是被拖的那一张（$expectedSize）" ((@($received | ForEach-Object { Get-ImageSize $_ }) -join ',') -eq $expectedSize)
        Check '日志里没有多图拖拽记录（走的是原单张路径）' ($newLog -notmatch '已构造多图拖拽数据')
        Check '单张拖出后面板照常收起' (-not [MultiDragProbe]::IsWindowVisible($script:Hwnd))
    }

    # ---------- 用例 nodrag：Ctrl 按住拖动不启动拖拽（有意取舍）----------
    if ($Mode -in @('all', 'nodrag')) {
        Write-Output ''
        Write-Output '== 用例 nodrag（Ctrl 按住拖动：不启动拖拽、不产生任何输出）=='

        # 注意顺序：先让面板显示出来，再放哨兵 —— 面板隐藏时 UIA 读不到卡片，
        # "哨兵已入库"会假失败（这一条踩过一次）
        [void](Ensure-Panel)

        # 重新放一个哨兵：Ctrl 拖动若"顺手"走了激活分支，剪切板一定会被改写
        $sentinel2 = 'MULTIDRAG-SENTINEL-NODRAG'
        Check '哨兵文本已入库' (Set-ClipboardSentinel $sentinel2)

        $cards = @(Get-Cards)
        $imgLast = (Get-ImageCards $cards)[-1]
        $before = Get-SelectionCount

        $dropsBefore = @(Get-Drops).Count
        $logLines = Get-LogLineCount
        [MultiDragProbe]::CtrlDrag($imgLast.CX, $imgLast.CY, $dropX, $dropY)
        Start-Sleep -Milliseconds 1500

        $newLog = Get-NewLog $logLines
        Check '目标什么都没收到（没有启动拖拽）' ((@(Get-Drops).Count) -eq $dropsBefore) "落下次数=$(@(Get-Drops).Count)"
        Check '日志里没有多图拖拽记录' ($newLog -notmatch '已构造多图拖拽数据')
        Check '面板保持显示' ([MultiDragProbe]::IsWindowVisible($script:Hwnd))
        Check '剪切板内容原样（没有顺手复制）' (((Get-Clipboard -Raw).Trim()) -eq $sentinel2) "现在=[$((Get-Clipboard -Raw).Trim())]"
        # 松手发生在面板外，这次手势不会产生任何输出；计数保持原样即可（值本身不作断言）
        Write-Output "      观察：手势前后计数 $before → $(Get-SelectionCount)（不产生输出即为正确）"
        Save-Shot '05-ctrl-drag-noop.png'
    }

    # ---------- 收尾：把最终状态留一张截图 ----------
    if ($Mode -eq 'all') {
        [void](Ensure-Panel)
        Save-Shot '06-final.png'
    }
} finally {
    if (-not $KeepApp) {
        if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        if ($receiver) { Stop-Process -Id $receiver.Id -Force -ErrorAction SilentlyContinue }
    }
}

if ($script:Failures -eq 0) {
    Write-Output ''
    Write-Output "断言合计：$($script:Checks) 项（通过 $($script:Checks - $script:Failures) 项，失败 0 项）"
    Write-Output 'RESULT: PASS'
    exit 0
} else {
    Write-Output ''
    Write-Output "断言合计：$($script:Checks) 项（通过 $($script:Checks - $script:Failures) 项，失败 $($script:Failures) 项）"
    Write-Output "RESULT: FAIL — $($script:Failures) 项未通过"
    exit 1
}
