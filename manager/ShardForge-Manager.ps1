# ShardForge Manager window (Windows PowerShell 5.1, Windows Forms). Start ShardForge-Manager.bat.
# Every action runs through ShardForge-Manage.ps1 in its own process, so the window never freezes and the same
# actions work from a console. The window itself only reads: the server's control file and log.
[CmdletBinding()]
param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$cli = Join-Path $PSScriptRoot 'ShardForge-Manage.ps1'
$controlFile = Join-Path $root 'data\control\server.json'
$configFile = Join-Path $PSScriptRoot 'manager.json'

$form = New-Object Windows.Forms.Form
$form.Text = 'ShardForge Manager'
$form.Size = New-Object Drawing.Size(940, 640)
$form.MinimumSize = New-Object Drawing.Size(760, 480)
$form.StartPosition = 'CenterScreen'
$form.BackColor = [Drawing.Color]::FromArgb(24, 30, 34)
$form.ForeColor = [Drawing.Color]::FromArgb(230, 224, 210)
$gold = [Drawing.Color]::FromArgb(214, 170, 76)

$head = New-Object Windows.Forms.Panel
$head.Dock = 'Top'; $head.Height = 74; $head.Padding = New-Object Windows.Forms.Padding(14, 10, 14, 6)
$title = New-Object Windows.Forms.Label
$title.Text = 'ShardForge'; $title.Font = New-Object Drawing.Font('Georgia', 16, [Drawing.FontStyle]::Bold); $title.ForeColor = $gold
$title.AutoSize = $true; $title.Location = New-Object Drawing.Point(14, 8)
$status = New-Object Windows.Forms.Label
$status.AutoSize = $false; $status.Location = New-Object Drawing.Point(14, 40); $status.Size = New-Object Drawing.Size(900, 22)
$status.Anchor = 'Top, Left, Right'; $status.Font = New-Object Drawing.Font('Segoe UI', 10)
$status.Text = 'Reading status...'
$head.Controls.Add($title); $head.Controls.Add($status)

$bar = New-Object Windows.Forms.FlowLayoutPanel
$bar.Dock = 'Top'; $bar.Height = 84; $bar.Padding = New-Object Windows.Forms.Padding(10, 4, 10, 4)

$output = New-Object Windows.Forms.TextBox
$output.Multiline = $true; $output.ReadOnly = $true; $output.ScrollBars = 'Both'; $output.WordWrap = $false
$output.Dock = 'Fill'; $output.Font = New-Object Drawing.Font('Consolas', 10)
$output.BackColor = [Drawing.Color]::FromArgb(14, 19, 22); $output.ForeColor = [Drawing.Color]::FromArgb(222, 216, 200)
$output.BorderStyle = 'None'

$foot = New-Object Windows.Forms.FlowLayoutPanel
$foot.Dock = 'Bottom'; $foot.Height = 34; $foot.Padding = New-Object Windows.Forms.Padding(10, 6, 10, 4)
$pre = New-Object Windows.Forms.CheckBox
$pre.Text = 'Include pre-releases when looking for updates'; $pre.AutoSize = $true
$foot.Controls.Add($pre)

$form.Controls.Add($output); $form.Controls.Add($foot); $form.Controls.Add($bar); $form.Controls.Add($head)

$script:job = $null; $script:result = $null; $script:resultPos = 0; $script:buttons = @()
$script:latest = $null; $script:liveLog = $false; $script:logFile = $null; $script:logStamp = [DateTime]::MinValue; $script:jobAction = ''

function Add-Text([string]$text) { $output.AppendText($text.Replace("`n", "`r`n").Replace("`r`r", "`r")) }

function Invoke-Action([string]$action, [string]$version = '') {
    if ($script:job) { return }
    $script:liveLog = $false
    $dir = Join-Path $root 'updates\manager-output'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $script:result = Join-Path $dir ([Guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($script:result, '')
    $script:resultPos = 0
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'Manager-Action.ps1') + '" -Action ' + $action + ' -ResultPath "' + $script:result + '"'
    if ($version) { $psi.Arguments += ' -Version ' + $version }
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $script:job = [Diagnostics.Process]::Start($psi)
    $script:jobAction = $action
    Add-Text ("`r`n> " + $action + "`r`n")
    foreach ($b in $script:buttons) { $b.Enabled = $false }
}

function Read-Result {
    if (-not $script:result -or -not (Test-Path -LiteralPath $script:result)) { return }
    $fs = [IO.File]::Open($script:result, 'Open', 'Read', 'ReadWrite')
    try {
        $null = $fs.Seek($script:resultPos, 'Begin')
        $r = New-Object IO.StreamReader($fs)
        $text = $r.ReadToEnd(); $script:resultPos = $fs.Position
        if ($text) { Add-Text $text }
    } finally { $fs.Dispose() }
}

function Add-Button([string]$text, [scriptblock]$onClick, [int]$width = 120) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $text; $b.Width = $width; $b.Height = 32; $b.FlatStyle = 'Flat'
    $b.FlatAppearance.BorderColor = [Drawing.Color]::FromArgb(70, 82, 88)
    $b.BackColor = [Drawing.Color]::FromArgb(34, 42, 47); $b.ForeColor = $form.ForeColor
    $b.Add_Click($onClick)
    $bar.Controls.Add($b); $script:buttons += $b
    return $b
}

$null = Add-Button 'Start' { Invoke-Action 'Start' }
$null = Add-Button 'Stop' { Invoke-Action 'Stop' }
$null = Add-Button 'Restart' { Invoke-Action 'Restart' }
$null = Add-Button 'Open dashboard' { Invoke-Action 'Open' } 130
$logBtn = Add-Button 'Live log' {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'data\logs') -Filter 'server-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if (-not $files.Count) { Add-Text "`r`nNo log yet: ShardForge writes one in data\logs once it has run.`r`n"; return }
    $script:liveLog = $true; $script:logFile = $files[0].FullName; $script:logStamp = [DateTime]::MinValue
}
$null = Add-Button 'Open folder' { Start-Process explorer.exe $root }
$null = Add-Button 'Firewall' {
    if ([Windows.Forms.MessageBox]::Show("Let other computers on your home or work network reach ShardForge?`r`n`r`nWindows asks for permission once. Public networks (cafés, hotels) stay closed.", 'Firewall', 'YesNo', 'Question') -eq 'Yes') { Invoke-Action 'Firewall' }
}
$checkBtn = Add-Button 'Check for updates' { Invoke-Action 'Check' } 150
$updateBtn = Add-Button 'Update' {
    $msg = if ($script:latest) { "Update ShardForge to $($script:latest)?" } else { 'Update ShardForge to the newest release?' }
    $msg += "`r`n`r`nIf it's running it stops and starts again (anyone in Walk is reconnected). Your data, settings and edits stay as they are; the database is copied aside first and the current version is kept for Rollback."
    if ([Windows.Forms.MessageBox]::Show($msg, 'Update ShardForge', 'YesNo', 'Question', 'Button2') -eq 'Yes') { Invoke-Action 'Update' }
}
$null = Add-Button 'Rollback' {
    if ([Windows.Forms.MessageBox]::Show("Go back to the version before the last update?`r`n`r`nYour data stays as it is.", 'Rollback', 'YesNo', 'Warning', 'Button2') -eq 'Yes') { Invoke-Action 'Rollback' }
}

# Pre-releases: remembered in manager.json.
try { $cfg = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json; $pre.Checked = [bool]$cfg.includePrereleases } catch { }
$pre.Add_CheckedChanged({
    try {
        $cfg = if (Test-Path -LiteralPath $configFile) { Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{ repository = 'kakelakel/ShardForge'; apiBase = 'https://api.github.com' } }
        $cfg | Add-Member -NotePropertyName includePrereleases -NotePropertyValue $pre.Checked -Force
        $cfg | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
    } catch { Add-Text ("`r`nCouldn't save the setting: " + $_.Exception.Message + "`r`n") }
})

function Update-Status {
    $installed = 'unknown'
    $vf = Join-Path $root 'VERSION'
    if (Test-Path -LiteralPath $vf) { $installed = (Get-Content -LiteralPath $vf -Raw).Trim() }
    $running = $null
    if (Test-Path -LiteralPath $controlFile) {
        try {
            $info = Get-Content -LiteralPath $controlFile -Raw | ConvertFrom-Json
            $p = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
            if ($p -and ((Split-Path -Parent $info.exe) -eq $root)) { $running = $info }
        } catch { }
    }
    $text = if ($running) {
        $up = ''; try { $up = '  |  up ' + ([DateTime]::UtcNow - [DateTime]::Parse($running.started).ToUniversalTime()).ToString('d\.hh\:mm') } catch { }
        "Running $($running.version) at $($running.url)  |  process $($running.pid)$up"
    } else { "Not running  |  installed $installed" }
    if ($script:latest) { $text += "  |  newest on GitHub: $($script:latest)" }
    $status.Text = $text
    $status.ForeColor = if ($running) { [Drawing.Color]::FromArgb(143, 194, 122) } else { $form.ForeColor }
}

$timer = New-Object Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({
    if ($script:job) {
        Read-Result
        if ($script:job.HasExited) {
            Read-Result
            if ($script:jobAction -eq 'Check') {
                $last = (Get-Content -LiteralPath $script:result -Raw)
                if ($last -match 'new version is out: (\S+)') { $script:latest = $Matches[1] } elseif ($last -match 'newest version') { $script:latest = $null }
            }
            if ($script:job.ExitCode -ne 0) { Add-Text "(failed)`r`n" }
            $script:job.Dispose(); $script:job = $null
            try { Remove-Item -LiteralPath $script:result -Force } catch { }
            foreach ($b in $script:buttons) { $b.Enabled = $true }
        }
    }
    if ($script:liveLog -and $script:logFile) {
        $f = Get-Item -LiteralPath $script:logFile -ErrorAction SilentlyContinue
        if ($f -and $f.LastWriteTimeUtc -ne $script:logStamp) {
            $script:logStamp = $f.LastWriteTimeUtc
            $output.Text = (Get-Content -LiteralPath $f.FullName -Tail 300) -join "`r`n"
            $output.SelectionStart = $output.TextLength; $output.ScrollToCaret()
        }
    }
    Update-Status
})
$form.Add_Shown({ if (-not $SmokeTest) { $timer.Start(); Update-Status; Invoke-Action 'Check' } })
$form.Add_FormClosing({
    if ($script:job) { $_.Cancel = $true; [Windows.Forms.MessageBox]::Show('An action is still running. Wait for it to finish before closing.', 'ShardForge Manager') | Out-Null }
    else { $timer.Stop() }
})

if ($SmokeTest) {
    # Build and paint the window off-screen without running anything.
    $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(-3000, 0); $form.ShowInTaskbar = $false
    $form.Show(); [Windows.Forms.Application]::DoEvents()
    if ($bar.Controls.Count -ne 9) { throw 'Manager buttons missing.' }
    Update-Status
    $bmp = New-Object Drawing.Bitmap($form.Width, $form.Height)
    $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
    New-Item -ItemType Directory -Path (Join-Path $root 'updates') -Force | Out-Null
    $bmp.Save((Join-Path $root 'updates\manager-window.png'), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $form.Dispose(); $timer.Dispose()
    'ShardForge Manager window: PASS'
} else { [Windows.Forms.Application]::Run($form) }
