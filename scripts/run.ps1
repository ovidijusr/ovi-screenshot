[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepository = Get-OviRepositoryRoot
$taskExecutable = Join-Path $taskRepository "artifacts\windows\$Configuration\app\Ovi.Screenshot.exe"
$taskCurrentApp = Join-Path $taskRepository "artifacts\windows\$Configuration\current-app.txt"
if (Test-Path -LiteralPath $taskCurrentApp) { $taskExecutable = [System.IO.File]::ReadAllText($taskCurrentApp).Trim() }
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw 'Build the app first with scripts\build.ps1.' }
Push-Location -LiteralPath (Split-Path -Parent $taskExecutable)
try { & $taskExecutable }
finally { Pop-Location }
