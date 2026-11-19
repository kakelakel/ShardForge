# ShardForge Manager: everything the Manager window does goes through here (and works on its own, too).
#
#   ShardForge-Manage.ps1 -Action Status [-AsJson]
#   ShardForge-Manage.ps1 -Action Start | Stop | Restart
#   ShardForge-Manage.ps1 -Action Check                     # is there a newer release on GitHub?
#   ShardForge-Manage.ps1 -Action Update [-Version v0.5.0]   # download, verify, stop, swap, start again
#   ShardForge-Manage.ps1 -Action Rollback                  # back to the version before the last update
#   ShardForge-Manage.ps1 -Action Logs [-Lines 200]
#   ShardForge-Manage.ps1 -Action Open                      # the dashboard in your browser
#
# Updates replace only the program files. The data folder (accounts, projects, maps, edits, backups, logs) and
# your appsettings.json are never touched; the database is copied aside before every update, and the replaced
# program files are kept for Rollback. Works with Windows PowerShell 5.1 and PowerShell 7 (Windows or Linux).
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Start', 'Stop', 'Restart', 'Check', 'Update', 'Rollback', 'Logs', 'Open', 'Firewall')]
    [string]$Action = 'Status',
    [string]$Version = '',
    [int]$Lines = 200,
    [switch]$AsJson,
    [switch]$Force,
    [string]$Root = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path -LiteralPath $Root).Path
Set-Location -LiteralPath $Root  # never hold the manager folder open: updates replace it
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$ext = if ($onWindows) { '.exe' } else { '' }
$exe = Join-Path $Root ('ShardForge' + $ext)
$dataDir = Join-Path $Root 'data'
$controlFile = Join-Path $dataDir 'control/server.json'
$updatesDir = Join-Path $Root 'updates'
$previousDir = Join-Path $updatesDir 'previous'
$configFile = Join-Path $PSScriptRoot 'manager.json'
# The program files an update brings and replaces. Everything else in the folder is yours and stays.
$programFiles = @(('ShardForge' + $ext), ('shardforge-cli' + $ext), 'VERSION', 'QUICKSTART.txt', 'LICENSE.txt', 'samples', 'manager', 'ShardForge-Manager.bat', 'shardforge-manager.sh', 'appsettings.default.json')

function Get-Config {
    $c = @{ repository = 'kakelakel/ShardForge'; includePrereleases = $false; apiBase = 'https://api.github.com' }
    if (Test-Path -LiteralPath $configFile) {
        $j = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
        foreach ($p in $j.PSObject.Properties) { $c[$p.Name] = $p.Value }
    }
    return $c
}

function Get-InstalledVersion {
    $f = Join-Path $Root 'VERSION'
    if (Test-Path -LiteralPath $f) { return (Get-Content -LiteralPath $f -Raw).Trim() }
    if ($onWindows -and (Test-Path -LiteralPath $exe)) { $v = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion; if ($v) { return ($v -split '\+')[0] } }
    return 'unknown'
}

function ConvertTo-VersionObject([string]$text) {
    $t = ($text -replace '^v', '') -replace '-.*$', ''
    $v = $null
    if ([Version]::TryParse($t, [ref]$v)) { return $v }
    return [Version]'0.0'
}

# The running server: the process named in the control file, if it's alive and is this folder's program.
function Get-Server {
    if (-not (Test-Path -LiteralPath $controlFile)) { return $null }
    try { $info = Get-Content -LiteralPath $controlFile -Raw | ConvertFrom-Json } catch { return $null }
    $p = Get-Process -Id $info.pid -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    try { if ($p.Path -and $info.exe -and ((Resolve-Path -LiteralPath $p.Path).Path -ne (Resolve-Path -LiteralPath $info.exe).Path)) { return $null } } catch { }
    if ($info.exe -and ((Split-Path -Parent $info.exe) -ne $Root)) { return $null }
    return [pscustomobject]@{ Process = $p; Info = $info }
}

function Get-Status([switch]$Online) {
    $s = Get-Server
    $o = [ordered]@{
        folder = $Root; installed = (Get-InstalledVersion); running = [bool]$s
        pid = $null; url = $null; uptime = $null; serverVersion = $null
        previous = $(if (Test-Path -LiteralPath (Join-Path $previousDir 'VERSION')) { (Get-Content -LiteralPath (Join-Path $previousDir 'VERSION') -Raw).Trim() } elseif (Test-Path -LiteralPath $previousDir) { 'unknown' } else { $null })
        latest = $null; updateAvailable = $null
    }
    if ($s) {
        $o.pid = $s.Process.Id; $o.url = $s.Info.url; $o.serverVersion = $s.Info.version
        try { $o.uptime = ([DateTime]::UtcNow - [DateTime]::Parse($s.Info.started).ToUniversalTime()).ToString('d\.hh\:mm\:ss') } catch { }
    }
    if ($Online) {
        try { $r = Get-Release; $o.latest = $r.tag_name; $o.updateAvailable = (ConvertTo-VersionObject $r.tag_name) -gt (ConvertTo-VersionObject $o.installed) }
        catch { $o.latest = 'unavailable: ' + $_.Exception.Message }
    }
    return [pscustomobject]$o
}

function Invoke-Api([string]$url) {
    if (-not $onWindows -or $PSVersionTable.PSVersion.Major -ge 6) { return Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'ShardForge-Manager'; Accept = 'application/vnd.github+json' } -TimeoutSec 30 }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    return Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'ShardForge-Manager'; Accept = 'application/vnd.github+json' } -TimeoutSec 30 -UseBasicParsing
}

function Save-Download([string]$url, [string]$path) {
    if ($onWindows -and $PSVersionTable.PSVersion.Major -lt 6) { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 }
    $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue' # the progress bar makes 5.1 downloads crawl
    try { Invoke-WebRequest -Uri $url -OutFile $path -Headers @{ 'User-Agent' = 'ShardForge-Manager' } -UseBasicParsing -TimeoutSec 600 }
    finally { $ProgressPreference = $old }
}

# The release to install: a given tag, or the newest (pre-releases only if the config says so).
function Get-Release([string]$tag = '') {
    $c = Get-Config
    $base = "$($c.apiBase)/repos/$($c.repository)/releases"
    if ($tag) { if ($tag -notmatch '^v') { $tag = 'v' + $tag }; return Invoke-Api "$base/tags/$tag" }
    $all = @(Invoke-Api "$base`?per_page=30") | Where-Object { -not $_.draft -and ($c.includePrereleases -or -not $_.prerelease) }
    if (-not $all) { throw "No releases on github.com/$($c.repository) yet." }
    return @($all | Sort-Object { ConvertTo-VersionObject $_.tag_name } -Descending)[0]
}

# Messages go to the host (information) stream: they never mix into what functions return, and the window
# captures them all the same.
function Write-Line([string]$text) { Write-Host $text }

function Start-Server {
    if (Get-Server) { Write-Line 'ShardForge is already running.'; return }
    if (-not (Test-Path -LiteralPath $exe)) { throw "No ShardForge program in $Root." }
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--ShardForge:OpenBrowser=false --ShardForge:Detached=true'
    $psi.WorkingDirectory = $Root
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    # Its own pipes, closed at once: the server must not hold this Manager's output open (it writes to its log).
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close(); $p.StandardOutput.Close(); $p.StandardError.Close()
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "ShardForge stopped right away (exit code $($p.ExitCode)). See the log: -Action Logs." }
        $s = Get-Server
        if ($s -and $s.Process.Id -eq $p.Id) { Write-Line "ShardForge $($s.Info.version) is running: $($s.Info.url) (process $($p.Id))."; return }
    }
    throw 'ShardForge started but did not report in within 45 seconds. See the log: -Action Logs.'
}

function Stop-Server {
    $s = Get-Server
    if (-not $s) { Write-Line 'ShardForge is not running.'; return $false }
    $ok = $false
    try {
        $null = Invoke-RestMethod -Method Post -Uri ($s.Info.url.TrimEnd('/') + '/api/control/stop') -Headers @{ 'X-ShardForge' = '1'; 'X-ShardForge-Control' = $s.Info.token } -TimeoutSec 10 -UseBasicParsing
        $ok = $s.Process.WaitForExit(30000)
    } catch { Write-Line ('The server did not answer the stop request: ' + $_.Exception.Message) }
    if (-not $ok) {
        if (-not $Force) { throw 'ShardForge did not stop within 30 seconds. Run Stop with -Force to end the process (the database is safe either way).' }
        $s.Process.Kill(); $null = $s.Process.WaitForExit(10000)
        Write-Line 'ShardForge was ended (forced).'
    } else { Write-Line 'ShardForge stopped.' }
    return $true
}

function Copy-ProgramFiles([string]$from, [string]$to) {
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    foreach ($n in $programFiles) {
        $src = Join-Path $from $n
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $dst = Join-Path $to $n
        if (Test-Path -LiteralPath $src -PathType Container) {
            # Folder contents into the folder (copying the folder onto an existing one would nest it).
            New-Item -ItemType Directory -Path $dst -Force | Out-Null
            Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
        } else { Copy-Item -LiteralPath $src -Destination $dst -Force }
    }
}

function Remove-ProgramFiles([string]$dir) {
    foreach ($n in $programFiles) {
        $p = Join-Path $dir $n
        if ($n -eq 'manager') { continue } # replaced file by file: this script runs from there
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
}

function Update-ShardForge {
    $installed = Get-InstalledVersion
    $r = Get-Release $Version
    if (-not $Version -and -not $Force -and (ConvertTo-VersionObject $r.tag_name) -le (ConvertTo-VersionObject $installed)) {
        Write-Line "You have the newest version ($installed; newest on GitHub: $($r.tag_name))."; return
    }
    $rid = if ($onWindows) { 'win-x64' } else { 'linux-x64' }
    $kind = if ($onWindows) { '.zip' } else { '.tar.gz' }
    $asset = @($r.assets | Where-Object { $_.name -like "ShardForge-*-$rid$kind" })[0]
    if (-not $asset) { throw "Release $($r.tag_name) has no $rid package." }
    $dl = Join-Path $updatesDir 'downloads'
    New-Item -ItemType Directory -Path $dl -Force | Out-Null
    $file = Join-Path $dl $asset.name
    Write-Line "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB) from $($r.tag_name)..."
    Save-Download $asset.browser_download_url $file
    # Integrity: the release's SHA256SUMS.txt must list this file with the same hash.
    $sums = @($r.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' })[0]
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sums) {
        $sumsFile = Join-Path $dl ($r.tag_name + '-SHA256SUMS.txt')
        Save-Download $sums.browser_download_url $sumsFile
        $line = Get-Content -LiteralPath $sumsFile | Where-Object { $_ -match ('\s\*?' + [regex]::Escape($asset.name) + '$') } | Select-Object -First 1
        if (-not $line) { throw "SHA256SUMS.txt doesn't list $($asset.name): not installing it." }
        if ($line.Split(' ')[0].ToLowerInvariant() -ne $hash) { Remove-Item -LiteralPath $file -Force; throw "The download doesn't match its checksum: not installing it." }
        Write-Line 'Checksum verified.'
    } elseif (-not $Force) { throw "Release $($r.tag_name) has no SHA256SUMS.txt to verify the download against. Use -Force to install it anyway." }
    else { Write-Line "No checksum file in this release; installing unverified (SHA256 $hash)." }

    # Unpack beside the install and check it's what we expect before touching anything.
    $stage = Join-Path $updatesDir 'staging'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    if ($onWindows) { Expand-Archive -LiteralPath $file -DestinationPath $stage -Force } else { & tar -xzf $file -C $stage; if ($LASTEXITCODE) { throw 'Could not unpack the download.' } }
    $new = @(Get-ChildItem -LiteralPath $stage -Directory)[0].FullName
    if (-not (Test-Path -LiteralPath (Join-Path $new ('ShardForge' + $ext)))) { throw 'The download has no ShardForge program in it.' }
    if (-not (Test-Path -LiteralPath (Join-Path $new 'VERSION'))) { Set-Content -LiteralPath (Join-Path $new 'VERSION') -Value ($r.tag_name -replace '^v', '') }
    # The new default settings ride along for reference; yours stay in use.
    if (Test-Path -LiteralPath (Join-Path $new 'appsettings.json')) { Move-Item -LiteralPath (Join-Path $new 'appsettings.json') -Destination (Join-Path $new 'appsettings.default.json') -Force }

    $wasRunning = [bool](Get-Server)
    if ($wasRunning) { $null = Stop-Server }
    # Data safety: a copy of the database and settings from before the update.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $snap = Join-Path $updatesDir "before-$installed-$stamp"
    New-Item -ItemType Directory -Path $snap -Force | Out-Null
    foreach ($n in @('shardforge.db', 'shardforge.db-wal', 'shardforge.db-shm', 'settings.json')) { $f = Join-Path $dataDir $n; if (Test-Path -LiteralPath $f) { Copy-Item -LiteralPath $f -Destination $snap } }
    if (Test-Path -LiteralPath (Join-Path $Root 'appsettings.json')) { Copy-Item -LiteralPath (Join-Path $Root 'appsettings.json') -Destination $snap }
    Write-Line "Database and settings copied to updates/$(Split-Path -Leaf $snap)."

    # Keep the current program for Rollback, then put the new one in place; undo on any failure.
    if (Test-Path -LiteralPath $previousDir) { Remove-Item -LiteralPath $previousDir -Recurse -Force }
    Copy-ProgramFiles $Root $previousDir
    if (-not (Test-Path -LiteralPath (Join-Path $previousDir 'VERSION'))) { Set-Content -LiteralPath (Join-Path $previousDir 'VERSION') -Value $installed }
    # The Manager's own settings (repository, pre-releases) are yours too: kept across updates.
    $keepConfig = if (Test-Path -LiteralPath $configFile) { [IO.File]::ReadAllText($configFile) } else { $null }
    try {
        Remove-ProgramFiles $Root
        Copy-ProgramFiles $new $Root
        if ($keepConfig) { [IO.File]::WriteAllText($configFile, $keepConfig) }
        if (-not (Test-Path -LiteralPath (Join-Path $Root 'appsettings.json')) -and (Test-Path -LiteralPath (Join-Path $Root 'appsettings.default.json'))) {
            Copy-Item -LiteralPath (Join-Path $Root 'appsettings.default.json') -Destination (Join-Path $Root 'appsettings.json')
        }
        if (-not $onWindows) { & chmod +x (Join-Path $Root 'ShardForge') (Join-Path $Root 'shardforge-cli') 2>$null }
    } catch {
        Write-Line ('Installing failed, putting the previous version back: ' + $_.Exception.Message)
        Remove-ProgramFiles $Root; Copy-ProgramFiles $previousDir $Root
        throw
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Line "Updated: $installed -> $(Get-InstalledVersion). The previous version is kept for Rollback."
    if ($wasRunning) { Start-Server }
}

function Undo-Update {
    if (-not (Test-Path -LiteralPath (Join-Path $previousDir ('ShardForge' + $ext)))) { throw 'There is no previous version to go back to.' }
    $installed = Get-InstalledVersion
    $wasRunning = [bool](Get-Server)
    if ($wasRunning) { $null = Stop-Server }
    $swap = Join-Path $updatesDir 'rollback-swap'
    if (Test-Path -LiteralPath $swap) { Remove-Item -LiteralPath $swap -Recurse -Force }
    $keepConfig = if (Test-Path -LiteralPath $configFile) { [IO.File]::ReadAllText($configFile) } else { $null }
    Copy-ProgramFiles $Root $swap
    Remove-ProgramFiles $Root
    Copy-ProgramFiles $previousDir $Root
    if ($keepConfig) { [IO.File]::WriteAllText($configFile, $keepConfig) }
    # The one rolled back from becomes the "previous", so Rollback again goes forward.
    Remove-Item -LiteralPath $previousDir -Recurse -Force
    Move-Item -LiteralPath $swap -Destination $previousDir
    Write-Line "Rolled back: $installed -> $(Get-InstalledVersion). Your data was not changed; a database copy from before each update is in the updates folder."
    if ($wasRunning) { Start-Server }
}

# One action at a time per install (the Manager window and a console can't both update at once).
$mutex = New-Object Threading.Mutex($false, ('Global\ShardForgeManager-' + [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.ToLowerInvariant()))).Replace('-', '').Substring(0, 16)))
$mutating = $Action -in @('Start', 'Stop', 'Restart', 'Update', 'Rollback')
if ($mutating -and -not $mutex.WaitOne(0)) { Write-Host 'Error: another Manager action is running for this ShardForge. Wait for it to finish.'; exit 1 }
$failed = $false
try {
    switch ($Action) {
        'Status' {
            $s = Get-Status
            if ($AsJson) { $s | ConvertTo-Json -Compress; break }
            if ($s.running) { Write-Line "ShardForge $($s.serverVersion) is running at $($s.url) (process $($s.pid), up $($s.uptime))." } else { Write-Line 'ShardForge is not running.' }
            Write-Line "Installed: $($s.installed) in $($s.folder)$(if ($s.previous) { "; previous for Rollback: $($s.previous)" })"
        }
        'Start' { Start-Server }
        'Stop' { $null = Stop-Server }
        'Restart' { $null = Stop-Server; Start-Server }
        'Check' {
            $s = Get-Status -Online
            if ($AsJson) { $s | ConvertTo-Json -Compress; break }
            if ($s.updateAvailable) { Write-Line "A new version is out: $($s.latest) (you have $($s.installed)). Run Update to install it." }
            elseif ($s.latest -like 'unavailable*') { Write-Line "Couldn't check for updates: $($s.latest -replace '^unavailable: ', '')" }
            else { Write-Line "You have the newest version ($($s.installed))." }
        }
        'Update' { Update-ShardForge }
        'Rollback' { Undo-Update }
        'Logs' {
            $f = @(Get-ChildItem -LiteralPath (Join-Path $dataDir 'logs') -Filter 'server-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
            if ($f.Count) { Get-Content -LiteralPath $f[0].FullName -Tail $Lines } else { Write-Line 'No log yet: the server writes one in data/logs once it has run.' }
        }
        'Firewall' {
            # Lets other computers on a private (home/work) network reach ShardForge's port. Windows only; asks for admin.
            if (-not $onWindows) { Write-Line 'On Linux, open the port with your firewall tool, e.g.: sudo ufw allow 2595/tcp'; break }
            $port = 2595
            $cfg = Join-Path $root 'appsettings.json'
            if (Test-Path -LiteralPath $cfg) { try { $u = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).Urls; if ($u -match ':(\d+)/?$') { $port = [int]$Matches[1] } } catch { } }
            $rule = "New-NetFirewallRule -DisplayName 'ShardForge (port $port)' -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Private,Domain | Out-Null; " +
                    "Get-NetFirewallRule -DisplayName 'ShardForge (port $port)' | Select-Object -Skip 1 | Remove-NetFirewallRule"
            $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-Command', $rule)
            if ($p.ExitCode -eq 0) { Write-Line "Done: computers on your private network can reach port $port. (Turn on 'Other computers on my network' in Admin, then Restart.)" }
            else { Write-Line "The firewall wasn't changed (exit $($p.ExitCode))." }
        }
        'Open' {
            $s = Get-Server
            $url = if ($s) { $s.Info.url } else { 'http://localhost:2595' }
            if ($onWindows) { Start-Process $url } else { & xdg-open $url 2>$null }
            Write-Line "Opened $url"
        }
    }
} catch {
    Write-Host ('Error: ' + $_.Exception.Message)
    $failed = $true
} finally { if ($mutating) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
if ($failed) { exit 1 }
