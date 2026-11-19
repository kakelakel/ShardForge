# Runs one Manager action for the window, writing everything it says to a file the window reads as it goes.
param([Parameter(Mandatory = $true)][string]$Action, [Parameter(Mandatory = $true)][string]$ResultPath, [string]$Version = '', [switch]$Force)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot 'ShardForge-Manage.ps1'
$code = 0
try {
    $global:LASTEXITCODE = 0
    $extra = @{}
    if ($Version) { $extra.Version = $Version }
    if ($Force) { $extra.Force = $true }
    & $cli -Action $Action @extra *>&1 | ForEach-Object { [IO.File]::AppendAllText($ResultPath, [string]$_ + [Environment]::NewLine) }
    $code = $LASTEXITCODE
} catch {
    [IO.File]::AppendAllText($ResultPath, 'Error: ' + $_.Exception.Message + [Environment]::NewLine)
    $code = 1
}
exit $code
