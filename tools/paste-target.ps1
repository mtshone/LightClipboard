# 一个极简的 WinForms 粘贴目标：内容变化时写入 %TEMP%\paste-target.txt，
# 用于端到端验证“点击卡片 -> 复制 -> 自动 Ctrl+V 粘贴到前一个窗口”。
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$out = Join-Path $env:TEMP 'paste-target.txt'
Set-Content -Path $out -Value '' -Encoding UTF8

$form = New-Object System.Windows.Forms.Form
$form.Text = 'LightClipboard Paste Target'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(180, 160)
$form.Size = New-Object System.Drawing.Size(720, 460)
$form.TopMost = $true

$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Consolas', 12)
$tb.ScrollBars = 'Vertical'
$tb.Add_TextChanged({ Set-Content -Path $out -Value $tb.Text -Encoding UTF8 })
$form.Controls.Add($tb)

$form.Add_Shown({
        $form.WindowState = 'Normal'
        $form.Activate()
        $tb.Focus()
        Set-Content -Path (Join-Path $env:TEMP 'paste-target-handle.txt') -Value "$($form.Handle)" -Encoding UTF8
        Write-Output "paste target ready handle=$($form.Handle)"
    })

[void]$form.Show()
[System.Windows.Forms.Application]::Run($form)
