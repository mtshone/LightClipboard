# 设置页排版回归脚本（开发期工具，不参与程序运行）
#
# 验证「有右对齐开关的设置行，说明文字不会压到开关上」（resources/04-实现细节与坑点.md 坑点 18）。
#
# 为什么要有这个脚本：这类缺陷**不报错、不影响功能**，只是文字被开关盖住半行；而它是否出现
# 取决于「文案够不够长」——将来给某一行加长说明文字，或把 ToggleSwitch 换成更宽的样式，
# 就会重新踩中，靠肉眼翻设置页很容易漏看。这里用 UI Automation 把几何断言掉。
#
# 判据（关键是**不能用"文字块宽度当有没有压上"**：文字块宽度永远是整行宽，那正是坑点本身）：
#   1. 结构：标题列的排版宽度必须整体避开开关 —— 列右沿到开关左沿留 >= 8px（给开关留了 56、
#      开关实际占 40，正常应余 16px）。这条与文字怎么折行无关，是坑点 18 的根因所在；
#   2. 像素：真正画出来的字也不能压到开关上 —— 在文字块的竖直范围内逐行从右往左找第一个
#      "非背景"像素（扫描上界卡在开关左沿，开关自己的图元不会被算成文字），余量同样 >= 8px。
# 两条都要过：只按像素判会漏 —— 文案刚好折在开关左边时（实测只差 1px）就不报，
# 换个宽度或改几个字就压进去了，而那时用户才会看到"文字被开关盖住半行"。
#
# 宽度分档：每档都**先把窗口尺寸写进 settings.json 再重启**，而不是用 SetWindowPos 现改 ——
# 现改会踩到 WPF 的"拖拽式缩放还没重新测量"状态（页脚会重影到设置页中间），
# 量出来的行号会对不上。默认 470（XAML 默认值）与 467（用户实测截图尺寸）。
#
# 前提：先 dotnet build（脚本跑的是本仓库 Debug 产物），且当前没有其它 LightClipboard 在运行
#       （单实例互斥体会互相干扰）。脚本自建隔离数据目录，用 --demo 起自己的实例，不动真实历史。
#
# 用法：
#   pwsh -File tools\settings-layout-test.ps1
#   pwsh -File tools\settings-layout-test.ps1 -ShotDir D:\tmp\shots     # 另外留存核对截图
#   pwsh -File tools\settings-layout-test.ps1 -Widths 380,470           # 只跑指定宽度
#
# 断言失败时退出码为 1，可直接接进 CI/发布前检查。
[CmdletBinding()]
param(
    # 默认用本仓库的 Debug 产物（按脚本位置推导，仓库搬到哪台机器都能用）
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LightClipboard\bin\Debug\net10.0-windows\LightClipboard.exe'),
    [string]$DataHome = "$env:TEMP\LightClipboard-LayoutCheck",
    [string]$ShotDir = '',
    # 逐档核对的窗口宽度（物理像素；当前 DPI 为 100% 时与 DIP 相同）
    [int[]]$Widths = @(470, 467)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

if (-not ('SettingsLayoutProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SettingsLayoutProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
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

# ---------------------------------------------------------------- 断言
$script:Failures = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Output "  [PASS] $Name$(if ($Detail) { "  ($Detail)" })" }
    else { $script:Failures++; Write-Output "  [FAIL] $Name$(if ($Detail) { "  ($Detail)" })" }
}

# ---------------------------------------------------------------- 工具函数
function Get-Lum([System.Drawing.Color]$c) { return (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) }

# 文字实际排版到哪：在文字块竖直范围内逐行从右往左找第一个"非背景"像素。
# 扫描上界卡在 (开关左沿 - 1)，开关自己的图元因此不会被算成文字。
function Measure-TextRight {
    param([System.Drawing.Bitmap]$Bmp, [int]$Left, [int]$Top, [int]$ScanRight, [int]$Bottom, [double]$Bg)
    $right = $Left
    for ($y = $Top; $y -le $Bottom; $y++) {
        for ($x = $ScanRight; $x -ge $Left; $x--) {
            if ([math]::Abs((Get-Lum $Bmp.GetPixel($x, $y)) - $Bg) -gt 20) {
                if ($x -gt $right) { $right = $x }
                break
            }
        }
    }
    return $right
}

# 说明文字"摊平成一行的自然宽度"（估算）：中日韩字符按字号计宽，其余按 0.55 字号。
# 只用来判断某一行**需不需要**给开关让位，不参与精度断言 —— 估算偏大只会让检查更严，
# 不会漏报。
function Get-NaturalTextWidth {
    param([string]$Text, [double]$FontSize = 11)
    $wide = 0; $narrow = 0
    foreach ($ch in $Text.ToCharArray()) {
        if ([int]$ch -gt 0x2E80) { $wide++ } else { $narrow++ }
    }
    return [int]($wide * $FontSize + $narrow * $FontSize * 0.55)
}

function Wait-Panel([int]$ProcId, [int]$TimeoutMs = 20000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $h = [SettingsLayoutProbe]::FindMain([uint32]$ProcId, 'LightClipboard')
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}

function Start-App([string]$DataHomePath) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '--demo'
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LIGHTCLIPBOARD_HOME'] = $DataHomePath
    $script:App = [System.Diagnostics.Process]::Start($psi)
    $h = Wait-Panel -ProcId $script:App.Id
    if ($h -eq [IntPtr]::Zero) { Write-Output 'RESULT: FAIL — 未找到面板主窗口'; exit 1 }
    return $h
}

function Stop-App {
    if ($script:App -and -not $script:App.HasExited) {
        try { $script:App.Kill(); $script:App.WaitForExit(5000) } catch { }
        Start-Sleep -Milliseconds 700   # 等单实例互斥体释放
    }
    $script:App = $null
}

# ---------------------------------------------------------------- 一档宽度的核对
function Test-OneWidth {
    param([int]$Width, [int]$Height = 700)

    # 关掉"失焦即隐藏"→「始终置顶」可用；否则置灰（用户截图里就是置灰那份）。两种都量。
    foreach ($hideOnDeactivate in @($true, $false)) {
        Stop-App
        @"
{
  "MaxItems": 300, "HotkeyEnabled": true, "EnableWinVHook": true,
  "AutoPaste": true, "HideOnDeactivate": $($hideOnDeactivate.ToString().ToLower()),
  "KeepOnTopWhenUnfocused": false, "DeferredHideDelayMs": 700, "RunAtStartup": false,
  "Theme": 2, "ThumbnailWidth": 320, "CopyImageWithFilePath": false, "MonitorPaused": false,
  "WindowWidth": $Width, "WindowHeight": $Height
}
"@ | Set-Content -LiteralPath (Join-Path $DataHome 'settings.json') -Encoding UTF8

        $hwnd = Start-App -DataHomePath $DataHome
        [void][SettingsLayoutProbe]::ForceForeground($hwnd)
        Start-Sleep -Milliseconds 800

        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $chipCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $chip = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $chipCond) |
            Where-Object { $_.Current.Name -eq '设置' } | Select-Object -First 1
        if (-not $chip) { Write-Output 'RESULT: FAIL — 未找到「设置」胶囊'; exit 1 }
        $chip.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1200

        $rect = New-Object SettingsLayoutProbe+RECT
        [void][SettingsLayoutProbe]::GetWindowRect($hwnd, [ref]$rect)
        $winL = $rect.Left; $winT = $rect.Top
        $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top

        $bmp = New-Object System.Drawing.Bitmap -ArgumentList $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($winL, $winT, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
        $g.Dispose()
        if ($ShotDir) {
            $bmp.Save((Join-Path $ShotDir ("settings-w{0}-{1}.png" -f $Width, $(if ($hideOnDeactivate) { 'grayed' } else { 'enabled' }))),
                [System.Drawing.Imaging.ImageFormat]::Png)
        }

        # 开关 = class Button + TogglePattern + 独立开关尺寸（滚动条 Thumb 也是这个组合，按宽度排掉）
        $toggles = @(); $texts = @()
        foreach ($el in @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition))) {
            $c = $el.Current
            if ($c.IsOffscreen) { continue }
            if ($c.ClassName -eq 'Button') {
                foreach ($p in $el.GetSupportedPatterns()) {
                    if ($p.ProgrammaticName -match 'TogglePattern') {
                        if ($c.BoundingRectangle.Width -gt 60) { continue }
                        $toggles += [pscustomobject]@{
                            X = [int]($c.BoundingRectangle.X - $winL); Y = [int]($c.BoundingRectangle.Y - $winT)
                            W = [int]$c.BoundingRectangle.Width; H = [int]$c.BoundingRectangle.Height
                            Enabled = $c.IsEnabled
                        }
                    }
                }
            }
            elseif ($c.ClassName -eq 'TextBlock' -and $c.Name) {
                $texts += [pscustomobject]@{
                    X = [int]($c.BoundingRectangle.X - $winL); Y = [int]($c.BoundingRectangle.Y - $winT)
                    R = [int]($c.BoundingRectangle.Right - $winL); B = [int]($c.BoundingRectangle.Bottom - $winT)
                    Name = ($c.Name -replace "`r?`n", ' ⏎ ')
                }
            }
        }

        Write-Output ''
        Write-Output ("== 窗口 {0}x{1} · 「始终置顶」{2} ==" -f $w, $h, $(if ($hideOnDeactivate) { '置灰' } else { '可用' }))
        Check -Name '设置页找到 6 个右对齐开关' -Ok ($toggles.Count -eq 6) -Detail ("{0} 个" -f $toggles.Count)

        $bg = $null
        foreach ($t in ($toggles | Sort-Object Y)) {
            # 本行的说明文字 = **顶部与开关对齐**（开关竖直居中于整行）、且整体在开关左边的 TextBlock。
            # 卡"顶部对齐"而不是"竖直区间有交集"：否则页脚那两行小字会被当成某一行的文案。
            $row = $texts | Where-Object { $_.Y -ge ($t.Y - 14) -and $_.Y -le ($t.Y + 14) -and $_.X -lt $t.X } |
                Sort-Object Y
            if (-not $row) { continue }

            if ($null -eq $bg) {
                $samples = @()
                for ($y = $row[0].Y; $y -le $row[-1].B; $y++) {
                    for ($x = 18; $x -le 23; $x++) { $samples += (Get-Lum $bmp.GetPixel($x, $y)) }
                }
                $bg = ($samples | Measure-Object -Average).Average
            }

            $textRight = Measure-TextRight -Bmp $bmp -Left $row[0].X -Top $row[0].Y -ScanRight ($t.X - 1) -Bottom $row[-1].B -Bg $bg
            $gap = $t.X - $textRight
            $label = if ($row[0].Name.Length -gt 14) { $row[0].Name.Substring(0, 14) + '…' } else { $row[0].Name }

            # 先判断这一行**需不需要**给开关让位：自然宽度（摊平成一行的宽度）够不着开关的行
            # （比如"点击卡片后自动粘贴"）本来就不会压上去，对它提"必须留 56px"是过度要求。
            $hint = $row | Where-Object { $_.Name -ne $row[0].Name -and $_.Name.Length -gt 24 } | Select-Object -Last 1
            if ($null -eq $hint) { $hint = $row[-1] }
            $natural = Get-NaturalTextWidth -Text $hint.Name
            $atRisk = $natural -gt ($t.X - $row[0].X)

            # 判据一（结构）：有风险的行的标题列必须**整体**避开开关（留 >= 8px）。
            # 这条才是坑点 18 的根因，而且**与文字怎么折行无关** —— 只按"像素有没有压上去"判会漏：
            # 文案刚好折在开关左边时（实测只差 1px）就不报，换个宽度就压进去了。
            if ($atRisk) {
                $reserve = $t.X - $row[0].R
                Check -Name ("标题列整体避开开关：{0}" -f $label) -Ok ($reserve -ge 8) `
                    -Detail ("列右沿 {0} / 开关左沿 {1}，留 {2}px" -f $row[0].R, $t.X, $reserve)
            }

            # 判据二（像素）：真正画出来的字也不能压到开关上（留 >= 8px，抗锯齿毛边不算"压上"）
            Check -Name ("文字不压开关：{0}" -f $label) -Ok ($gap -ge 8) `
                -Detail ("余量 {0}px（文字右沿 {1} / 开关左沿 {2}，本项{3}{4}）" -f $gap, $textRight, $t.X,
                $(if ($t.Enabled) { '可用' } else { '置灰' }), $(if ($atRisk) { '，长文案' } else { '' }))
        }
        $bmp.Dispose()
    }
}

# ---------------------------------------------------------------- 主流程
if (Get-Process -Name 'LightClipboard' -ErrorAction SilentlyContinue) {
    Write-Output 'RESULT: FAIL — 已有 LightClipboard 实例在运行，请先退出（单实例互斥体会互相干扰）'
    exit 1
}
if (-not (Test-Path $Exe)) {
    Write-Output "RESULT: FAIL — 未找到可执行文件：$Exe（先 dotnet build）"
    exit 1
}
if ($ShotDir) { New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null }
Remove-Item -Recurse -Force $DataHome -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataHome | Out-Null

try {
    foreach ($width in $Widths) { Test-OneWidth -Width $width }
}
finally {
    Stop-App
}

Write-Output ''
if ($script:Failures -eq 0) { Write-Output 'RESULT: PASS'; exit 0 }
Write-Output ("RESULT: FAIL — {0} 项未通过" -f $script:Failures)
exit 1
