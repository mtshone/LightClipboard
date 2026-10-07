# 拖出（拖动输出）自动隐藏回归脚本（开发期工具，不参与程序运行）
#
# 验证的行为：把卡片拖到别的程序、并被对方真正接收后，面板应当**自动收起**；
#            取消拖拽（拖到一半按 Esc）或松手仍在面板内时，面板必须**保留**。
#
# 为什么要单独测：这是一段"看不见的行为"——拖拽期间窗口失焦会被 `_isDragging` 有意忽略
# （忽略掉才不会把拖拽打断），因此收尾必须在 `DoDragDrop` 返回后由 `OnListPreviewMouseMove`
# 补一次。任何一次"顺手删掉这段收尾"的改动都不会报错、不会崩，只会让面板一直停在屏幕上。
#
# 六个用例（前三项验拖出收尾，后三项验「失焦即隐藏」与「始终置顶」）：
#   accept     拖到接收目标 → 目标收到内容 + 面板隐藏 + 日志出现"拖出成功，面板已自动隐藏"
#   cancel     拖到接收目标后按 Esc 取消 → 面板保留（且没有"拖出成功"日志）
#   inside     面板内拖动并松手（卡片 1 → 卡片 2）→ 面板保留 + 日志出现"拖拽在面板内结束"
# 另外三个是"失焦即隐藏 / 始终置顶"开关相关用例（设置项一动就出错，肉眼很难发现）：
#   clickaway  设置默认开启：点击面板外 → 面板收起（正向对照）
#   off        把 settings.json 的 HideOnDeactivate 改成 false 后重启：拖出**不**收起、
#              点击面板外也**不**收起（这一条是"开关像没生效"的回归点）
#   ontop      再加 KeepOnTopWhenUnfocused=true：被同样置顶的窗口盖住后，面板应自动抬回最上层
#              （且不抢焦点）；`off` 用例里有一条反向对照——没开这个开关时确实会被压住
#
# 用法：
#   pwsh -File tools\dragout-test.ps1                 # 六个用例全跑
#   pwsh -File tools\dragout-test.ps1 -Mode accept    # 只跑一个用例
#
# 前置条件：退出正在使用的 LightClipboard 实例（单实例互斥体会互相干扰）。
# 脚本自建隔离数据目录（%TEMP%\LightClipboard-DragOutTest）并以 --demo 启动自己的实例，
# 同时拉起一个"接收方"窗口（WinForms 文本框，显式接受 UnicodeText，等价于记事本/聊天框）。
[CmdletBinding()]
param(
    [ValidateSet('all', 'accept', 'cancel', 'inside', 'clickaway', 'off', 'ontop')][string]$Mode = 'all',
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-DragOutTest",
    [switch]$KeepApp
)

$ErrorActionPreference = 'Stop'

if (-not ('DragOutProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DragOutProbe {
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
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }

    // 移动/缩放窗口（不改 z 序、不激活），用于把"接收方"摆到面板上方做遮挡测试
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const uint GA_ROOT = 2;

    public static void MoveResize(IntPtr h, int x, int y, int cx, int cy) {
        SetWindowPos(h, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // 某坐标处最上层的顶层窗口（用来判断"面板有没有被别的窗口盖住"）
    public static IntPtr TopWindowAt(int x, int y) {
        return GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);
    }

    // 主窗口必须按"标题 = LightClipboard 且类名以 HwndWrapper 开头"来找，
    // 只按标题会抓到确认对话框（#32770），量出来的矩形完全不对（07 篇已记录过这个坑）
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

    public static void SendCombo(byte m1, byte m2, byte key) {
        keybd_event(m1, 0, 0, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        if (m2 != 0) keybd_event(m2, 0, 2, UIntPtr.Zero);
        keybd_event(m1, 0, 2, UIntPtr.Zero);
    }

    // 真实鼠标单击（用于"点击面板外"这类失焦场景）
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    // 按住左键从 A 拖到 B。esc=true 时在松手前先按 Esc（OLE 会把它当成取消，效果为 None）
    public static void Drag(int fromX, int fromY, int toX, int toY, bool esc) {
        SetCursorPos(fromX, fromY);
        System.Threading.Thread.Sleep(200);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(250);
        for (int i = 1; i <= 20; i++) {
            SetCursorPos(fromX + (toX - fromX) * i / 20, fromY + (toY - fromY) * i / 20);
            System.Threading.Thread.Sleep(35);
        }
        System.Threading.Thread.Sleep(350);
        if (esc) {
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);    // VK_ESCAPE
            System.Threading.Thread.Sleep(60);
            keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            System.Threading.Thread.Sleep(300);
        }
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
    }
}
'@
}

[void][DragOutProbe]::SetProcessDPIAware()

$script:Failures = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) {
        Write-Output "  [PASS] $Name$(if ($Detail) { "  ($Detail)" })"
    } else {
        $script:Failures++
        Write-Output "  [FAIL] $Name$(if ($Detail) { "  ($Detail)" })"
    }
}

function Get-AppLogPath { Join-Path $DataHome ("Logs\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd')) }

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 4000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return [bool](& $Condition)
}

# 让面板成为前台窗口（"点击别处会收起"这类断言只有在面板确实持有焦点时才有意义）。
# 面板已经在前台就别按热键 —— TogglePanel 在"可见且激活"时会把它收起来。
function Ensure-PanelForeground([IntPtr]$Hwnd, [int]$AppPid) {
    if (([DragOutProbe]::ForegroundPid()) -eq $AppPid -and [DragOutProbe]::IsWindowVisible($Hwnd)) {
        return
    }

    [DragOutProbe]::SendCombo(17, 16, 86)   # Ctrl+Shift+V
    Start-Sleep -Milliseconds 1200
}

# 把"接收方"（本身是 TopMost 窗口）摆到面板上并点击它，模拟"被同样置顶的窗口盖住"。
# 面板本身是 Topmost，普通窗口盖不住它；被激活的**置顶**窗口才会排到置顶层最上面。
# 返回点击后"面板中心处最上层的窗口句柄"：等于面板 → 面板在最上层；否则 → 被压住了。
function Cover-PanelWithTarget([IntPtr]$TargetHwnd, [IntPtr]$PanelHwnd) {
    $prect = New-Object DragOutProbe+RECT
    [void][DragOutProbe]::GetWindowRect($PanelHwnd, [ref]$prect)
    $scale = [DragOutProbe]::GetDpiForWindow($PanelHwnd) / 96.0
    $margin = [int](80 * $scale)

    # 接收方要比面板大一圈，这样才有"在接收方上、但在面板外"的落点可点
    [DragOutProbe]::MoveResize(
        $TargetHwnd,
        $prect.Left - $margin,
        $prect.Top - $margin,
        ($prect.Right - $prect.Left) + 2 * $margin,
        ($prect.Bottom - $prect.Top) + 2 * $margin)
    Start-Sleep -Milliseconds 500

    [DragOutProbe]::Click($prect.Left - [int]($margin / 2), $prect.Top + [int](60 * $scale))
    Start-Sleep -Milliseconds 1400   # 等应用侧"始终置顶"的抬回动作走完

    return [DragOutProbe]::TopWindowAt(
        [int](($prect.Left + $prect.Right) / 2),
        [int](($prect.Top + $prect.Bottom) / 2))
}

$targetScript = Join-Path $DataHome 'drag-target.ps1'
$targetText = Join-Path $DataHome 'drag-target.txt'
$app = $null
$target = $null
$hwnd = [IntPtr]::Zero

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

    # ---------- 隔离数据目录 + 启动被测程序 ----------
    if (Test-Path $DataHome) { Remove-Item $DataHome -Recurse -Force }
    New-Item -ItemType Directory -Path $DataHome -Force | Out-Null

    # 接收方：WinForms 文本框，显式声明接受 UnicodeText（= 记事本 / 聊天框这类真实接收方）
    @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$out = Join-Path $env:TEMP 'LightClipboard-DragOutTest\drag-target.txt'
Set-Content -Path $out -Value '' -Encoding UTF8
$form = New-Object System.Windows.Forms.Form
$form.Text = 'LightClipboard Drag Target'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(120, 120)
$form.Size = New-Object System.Drawing.Size(640, 420)
$form.TopMost = $true
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true
$tb.Dock = 'Fill'
$tb.AllowDrop = $true
$tb.Font = New-Object System.Drawing.Font('Consolas', 12)
$tb.Add_DragEnter({ param($s, $e) if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::UnicodeText)) { $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy } })
$tb.Add_DragOver({ param($s, $e) if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::UnicodeText)) { $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy } })
$tb.Add_DragDrop({
        param($s, $e)
        if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::UnicodeText)) {
            $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy
            Set-Content -Path $out -Value ([string]$e.Data.GetData([System.Windows.Forms.DataFormats]::UnicodeText)) -Encoding UTF8
        }
    })
$form.Controls.Add($tb)
[void]$form.Show()
[System.Windows.Forms.Application]::Run($form)
'@ | Set-Content -Path $targetScript -Encoding UTF8

    $target = Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-File', $targetScript -WindowStyle Hidden -PassThru
    $env:LIGHTCLIPBOARD_HOME = $DataHome
    $app = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 4

    $hwnd = [DragOutProbe]::FindMainWindow('LightClipboard')
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Output 'RESULT: FAIL — 未找到面板主窗口'
        exit 1
    }

    $targetHwnd = [DragOutProbe]::FindWindowByTitle('LightClipboard Drag Target')
    if ($targetHwnd -eq [IntPtr]::Zero) {
        Write-Output 'RESULT: FAIL — 接收方窗口未就绪'
        exit 1
    }

    # ---------- 用例 ----------
    $cases = if ($Mode -eq 'all') { @('accept', 'cancel', 'inside') } elseif ($Mode -eq 'clickaway' -or $Mode -eq 'off') { @() } else { @($Mode) }
    $payloadIndex = 0

    foreach ($case in $cases) {
        Write-Output "== 用例 $case =="
        $payloadIndex++
        $payload = "DRAGOUT-$($case.ToUpper())-$payloadIndex"

        # 让第一条卡片就是本次要拖的文本（剪切板监听会把它置顶）
        Set-Clipboard -Value $payload
        Start-Sleep -Seconds 2

        # 鼠标先归位到主屏中部，再呼出面板 → 面板会落在该显示器右下角（定位逻辑读的是光标所在屏）
        [void][DragOutProbe]::SetCursorPos(1280, 720)
        if (-not [DragOutProbe]::IsWindowVisible($hwnd)) {
            [DragOutProbe]::SendCombo(17, 16, 86)   # Ctrl+Shift+V
            Start-Sleep -Milliseconds 1200
        }
        Start-Sleep -Milliseconds 600

        $visibleBefore = [DragOutProbe]::IsWindowVisible($hwnd)
        Check '拖拽前面板可见' $visibleBefore
        if (-not $visibleBefore) { continue }

        # 卡片 1 的屏幕坐标：面板顶部往下 176 DIP 处（标题栏 + 搜索框 + 胶囊 + 半张卡片），横向居中。
        # 用 DPI 缩放换算，换台缩放率不同的机器也不用重量坐标。
        $prect = New-Object DragOutProbe+RECT
        [void][DragOutProbe]::GetWindowRect($hwnd, [ref]$prect)
        $scale = [DragOutProbe]::GetDpiForWindow($hwnd) / 96.0
        $cardX = [int](($prect.Left + $prect.Right) / 2)
        $cardY = [int]($prect.Top + 176 * $scale)

        $trect = New-Object DragOutProbe+RECT
        [void][DragOutProbe]::GetWindowRect($targetHwnd, [ref]$trect)
        $dropX = [int](($trect.Left + $trect.Right) / 2)
        $dropY = [int](($trect.Top + $trect.Bottom) / 2)

        $logPath = Get-AppLogPath
        $logLines = if (Test-Path $logPath) { (Get-Content $logPath).Count } else { 0 }
        Set-Content -Path $targetText -Value '' -Encoding UTF8

        switch ($case) {
            'accept' { [DragOutProbe]::Drag($cardX, $cardY, $dropX, $dropY, $false) }
            'cancel' { [DragOutProbe]::Drag($cardX, $cardY, $dropX, $dropY, $true) }
            'inside' {
                $insideY = $cardY + [int](110 * $scale)   # 卡片 2 的位置，仍在面板内
                [DragOutProbe]::Drag($cardX, $cardY, $cardX, $insideY, $false)
            }
        }

        Start-Sleep -Milliseconds 1200
        $newLog = if (Test-Path $logPath) { (Get-Content $logPath | Select-Object -Skip $logLines) -join "`n" } else { '' }
        $received = if (Test-Path $targetText) { (Get-Content $targetText -Raw -Encoding UTF8).Trim() } else { '' }
        $visibleAfter = [DragOutProbe]::IsWindowVisible($hwnd)

        switch ($case) {
            'accept' {
                Check '接收方收到了内容' ($received -eq $payload) "收到=[$received]"
                Check '面板已自动隐藏' (-not $visibleAfter)
                Check '日志出现「拖出成功，面板已自动隐藏」' ($newLog -match '拖出成功，面板已自动隐藏')
            }
            'cancel' {
                Check '取消拖拽后面板保留' $visibleAfter
                Check '日志没有「拖出成功」' ($newLog -notmatch '拖出成功')
            }
            'inside' {
                Check '面板内松手后面板保留' $visibleAfter
                Check '日志出现「拖拽在面板内结束」' ($newLog -match '拖拽在面板内结束')
            }
        }
    }

    # ---------- 用例 clickaway：设置默认开启时，点击面板外应收起（"失焦即隐藏"的正向对照） ----------
    if ($Mode -in @('all', 'clickaway')) {
        Write-Output '== 用例 clickaway（失焦即隐藏=开启：点击面板外应收起面板）=='
        [void][DragOutProbe]::SetCursorPos(1280, 720)
        Ensure-PanelForeground $hwnd $app.Id
        Check '点击前面板是前台窗口' (([DragOutProbe]::ForegroundPid()) -eq $app.Id)

        $trect = New-Object DragOutProbe+RECT
        [void][DragOutProbe]::GetWindowRect($targetHwnd, [ref]$trect)
        [DragOutProbe]::Click([int](($trect.Left + $trect.Right) / 2), [int](($trect.Top + $trect.Bottom) / 2))
        Start-Sleep -Milliseconds 1500
        Check '点击面板外面板已收起' (-not [DragOutProbe]::IsWindowVisible($hwnd))
    }

    # ---------- 用例 settingoff：关闭「失焦即隐藏」后，拖出与点击别处都**不该**收起面板 ----------
    if ($Mode -in @('all', 'off')) {
        Write-Output '== 用例 settingoff（失焦即隐藏=关闭：拖出与点击别处都不该收起面板）=='

        # 设置只在启动时读一次，所以改写 settings.json 后必须重启应用
        if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 2
        @'
{ "MaxItems": 300, "HotkeyEnabled": true, "EnableWinVHook": false, "HideOnDeactivate": false, "Theme": 2 }
'@ | Set-Content -Path (Join-Path $DataHome 'settings.json') -Encoding UTF8

        $app = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
        Start-Sleep -Seconds 4
        $hwnd = [DragOutProbe]::FindMainWindow('LightClipboard')
        if ($hwnd -eq [IntPtr]::Zero) {
            Check '重启后找到面板窗口' $false
        } else {
            $payload = 'DRAGOUT-SETTINGOFF'
            Set-Clipboard -Value $payload
            Start-Sleep -Seconds 2
            [void][DragOutProbe]::SetCursorPos(1280, 720)
            Ensure-PanelForeground $hwnd $app.Id
            Check '拖拽前面板可见' ([DragOutProbe]::IsWindowVisible($hwnd))

            $prect = New-Object DragOutProbe+RECT
            [void][DragOutProbe]::GetWindowRect($hwnd, [ref]$prect)
            $scale = [DragOutProbe]::GetDpiForWindow($hwnd) / 96.0
            $cardX = [int](($prect.Left + $prect.Right) / 2)
            $cardY = [int]($prect.Top + 176 * $scale)
            $trect = New-Object DragOutProbe+RECT
            [void][DragOutProbe]::GetWindowRect($targetHwnd, [ref]$trect)
            $dropX = [int](($trect.Left + $trect.Right) / 2)
            $dropY = [int](($trect.Top + $trect.Bottom) / 2)

            $logPath = Get-AppLogPath
            $logLines = if (Test-Path $logPath) { (Get-Content $logPath).Count } else { 0 }
            Set-Content -Path $targetText -Value '' -Encoding UTF8

            # 1) 拖出到接收方：内容被接收，但面板必须保留
            [DragOutProbe]::Drag($cardX, $cardY, $dropX, $dropY, $false)
            Start-Sleep -Milliseconds 1200
            $newLog = if (Test-Path $logPath) { (Get-Content $logPath | Select-Object -Skip $logLines) -join "`n" } else { '' }
            $received = if (Test-Path $targetText) { (Get-Content $targetText -Raw -Encoding UTF8).Trim() } else { '' }
            Check '接收方收到了内容' ($received -eq $payload) "收到=[$received]"
            Check '设置关闭时拖出后面板保留' ([DragOutProbe]::IsWindowVisible($hwnd))
            Check '日志出现「失焦即隐藏」已关闭' ($newLog -match '「失焦即隐藏」已关闭')

            # 2) 点击面板外：同样必须保留（这一条正是"开关像没生效"的回归点）
            $logLines2 = (Get-Content $logPath).Count
            Ensure-PanelForeground $hwnd $app.Id
            Check '点击前面板是前台窗口' (([DragOutProbe]::ForegroundPid()) -eq $app.Id)
            [DragOutProbe]::Click($dropX, $dropY)
            Start-Sleep -Milliseconds 1500
            Check '设置关闭时点击面板外面板保留' ([DragOutProbe]::IsWindowVisible($hwnd))
            $logAfterClick = (Get-Content $logPath | Select-Object -Skip $logLines2) -join "`n"
            Check '点击后日志里没有「已隐藏」类记录' ($logAfterClick -notmatch '面板已自动隐藏')

            # 反向对照：没开「始终置顶」时，同样置顶的窗口盖上来就会把面板压住（这正是要修的现象）
            $onTop = Cover-PanelWithTarget $targetHwnd $hwnd
            Check '未开「始终置顶」时，被同样置顶的窗口压住（反向对照）' ($onTop -ne $hwnd)
        }
    }

    # ---------- 用例 ontop：始终置顶（失焦即隐藏关闭 + 始终置顶开启）----------
    if ($Mode -in @('all', 'ontop')) {
        Write-Output '== 用例 ontop（始终置顶=开启：被同样置顶的窗口盖住后应自动抬回最上层）=='

        if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 2
        @'
{ "MaxItems": 300, "HotkeyEnabled": true, "EnableWinVHook": false, "HideOnDeactivate": false, "KeepOnTopWhenUnfocused": true, "Theme": 2 }
'@ | Set-Content -Path (Join-Path $DataHome 'settings.json') -Encoding UTF8

        $app = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
        Start-Sleep -Seconds 4
        $hwnd = [DragOutProbe]::FindMainWindow('LightClipboard')
        if ($hwnd -eq [IntPtr]::Zero) {
            Check '重启后找到面板窗口' $false
        } else {
            [void][DragOutProbe]::SetCursorPos(1280, 720)
            Ensure-PanelForeground $hwnd $app.Id
            Check '面板可见且在前台' (([DragOutProbe]::ForegroundPid()) -eq $app.Id -and [DragOutProbe]::IsWindowVisible($hwnd))

            $logPath = Get-AppLogPath
            $logLines = if (Test-Path $logPath) { (Get-Content $logPath).Count } else { 0 }

            $onTop = Cover-PanelWithTarget $targetHwnd $hwnd
            Check '面板已被抬回最上层' ($onTop -eq $hwnd)
            Check '焦点没有被面板抢回（前台仍是那个窗口）' (([DragOutProbe]::ForegroundPid()) -eq $target.Id)
            $newLog = if (Test-Path $logPath) { (Get-Content $logPath | Select-Object -Skip $logLines) -join "`n" } else { '' }
            Check '日志出现「始终置顶」抬回记录' ($newLog -match '始终置顶')
        }
    }
} finally {
    if (-not $KeepApp) {
        if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        if ($target) { Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue }
    }
}

if ($script:Failures -eq 0) {
    Write-Output 'RESULT: PASS'
    exit 0
} else {
    Write-Output "RESULT: FAIL — $($script:Failures) 项未通过"
    exit 1
}
