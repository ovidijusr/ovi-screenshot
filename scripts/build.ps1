[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipTests
)

. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepository = Get-OviRepositoryRoot
Initialize-OviBuildEnvironment -RepositoryRoot $taskRepository
$taskProfile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
$taskRustDll = Join-Path $taskRepository "target\$taskProfile\ovi_core.dll"
$taskArtifacts = Join-Path $taskRepository "artifacts\windows\$Configuration"
$taskAppOutput = Join-Path $taskArtifacts 'app'
$taskSmokeOutput = Join-Path $taskArtifacts 'smoke'
$taskRecordingSmokeOutput = Join-Path $taskArtifacts 'recording-smoke'
$taskLiveApp = Get-Process -Name 'Ovi.Screenshot' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($taskAppOutput, [StringComparison]::OrdinalIgnoreCase) }
if ($taskLiveApp) {
    $taskAppOutput = Join-Path $taskArtifacts ('app-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

Push-Location -LiteralPath $taskRepository
try {
    $taskCargoArgs = @('build', '--workspace', '--locked')
    if ($Configuration -eq 'Release') { $taskCargoArgs += '--release' }
    Invoke-OviChecked 'cargo' $taskCargoArgs

    if (-not $SkipTests) {
        Invoke-OviChecked 'cargo' @('fmt', '--all', '--', '--check')
        Invoke-OviChecked 'cargo' @('test', '--workspace', '--locked')
        Invoke-OviChecked 'cargo' @('clippy', '--workspace', '--all-targets', '--locked', '--', '-D', 'warnings')
    }

    if (-not (Test-Path -LiteralPath $taskRustDll)) { throw "Rust DLL was not produced at $taskRustDll." }
    Invoke-OviChecked 'dotnet' @('build', 'apps/windows/Ovi.Screenshot.csproj', '--configuration', $Configuration, '--output', $taskAppOutput, '-p:RestoreLockedMode=true', '-p:Platform=x64', "-p:RustCorePath=$taskRustDll")
    Invoke-OviChecked 'dotnet' @('build', 'apps/windows/Smoke/Ovi.CoreSmoke.csproj', '--configuration', $Configuration, '--output', $taskSmokeOutput, "-p:RustCorePath=$taskRustDll")
    Invoke-OviChecked 'dotnet' @('build', 'apps/windows/RecordingSmoke/Ovi.RecordingSmoke.csproj', '--configuration', $Configuration, '--output', $taskRecordingSmokeOutput, '-p:Platform=x64')

    if (-not $SkipTests) {
        $taskSmokeProgram = Join-Path $taskSmokeOutput 'Ovi.CoreSmoke.exe'
        $taskSmokeImage = Join-Path $taskArtifacts 'core-smoke.png'
        Invoke-OviChecked $taskSmokeProgram @($taskSmokeImage)
        $taskRecordingSmoke = Join-Path $taskRecordingSmokeOutput 'Ovi.RecordingSmoke.exe'
        $taskRecordingImage = Join-Path $taskArtifacts ('recording-smoke-' + [Guid]::NewGuid().ToString('N') + '.mp4')
        Invoke-OviChecked $taskRecordingSmoke @($taskRecordingImage)
    }
    [System.IO.File]::WriteAllText((Join-Path $taskArtifacts 'current-app.txt'), (Join-Path $taskAppOutput 'Ovi.Screenshot.exe'))
}
finally { Pop-Location }

Write-Host "Windows app built: $(Join-Path $taskAppOutput 'Ovi.Screenshot.exe')"
Write-Host "Launch with: .\scripts\run.ps1 -Configuration $Configuration"
