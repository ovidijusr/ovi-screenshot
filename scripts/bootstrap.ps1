[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
$taskRepository = Get-OviRepositoryRoot
$taskTools = Join-Path $taskRepository '.tools'
$taskCargoHome = Join-Path $taskTools 'cargo'
$taskRustupHome = Join-Path $taskTools 'rustup'
$taskToolchainFile = Join-Path $taskRepository 'rust-toolchain.toml'
$taskToolchainMatch = Select-String -LiteralPath $taskToolchainFile -Pattern '^channel\s*=\s*"([^"]+)"'
if (-not $taskToolchainMatch) { throw 'No pinned Rust channel was found in rust-toolchain.toml.' }
$taskChannel = $taskToolchainMatch.Matches[0].Groups[1].Value

New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$env:CARGO_HOME = $taskCargoHome
$env:RUSTUP_HOME = $taskRustupHome
$taskRustup = Join-Path $taskCargoHome 'bin\rustup.exe'
if (-not (Test-Path -LiteralPath $taskRustup)) {
    $taskInstaller = Join-Path $taskTools 'rustup-init.exe'
    Write-Host 'Downloading official Rust installer for a workspace-local installation...'
    Invoke-WebRequest -Uri 'https://win.rustup.rs/x86_64' -OutFile $taskInstaller
    Invoke-OviChecked $taskInstaller @('-y', '--no-modify-path', '--profile', 'minimal', '--default-toolchain', $taskChannel)
}
else {
    Invoke-OviChecked $taskRustup @('toolchain', 'install', $taskChannel, '--profile', 'minimal')
}

Initialize-OviBuildEnvironment -RepositoryRoot $taskRepository
Push-Location -LiteralPath $taskRepository
try {
    Invoke-OviChecked 'cargo' @('--version')
    Invoke-OviChecked 'dotnet' @('--version')
}
finally { Pop-Location }
Write-Host 'Rust is ready inside .tools. Global PATH and user profiles were not modified.'
Write-Host 'Windows builds also require MSVC C++ tools and a Windows SDK. See docs/development.md.'
