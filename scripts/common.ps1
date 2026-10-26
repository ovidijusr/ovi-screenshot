Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OviRepositoryRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Initialize-OviBuildEnvironment {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $taskCargoHome = Join-Path $RepositoryRoot '.tools\cargo'
    $taskCargoBin = Join-Path $taskCargoHome 'bin'
    $taskCargo = Join-Path $taskCargoBin 'cargo.exe'
    if (Test-Path -LiteralPath $taskCargo) {
        $env:CARGO_HOME = $taskCargoHome
        $env:RUSTUP_HOME = Join-Path $RepositoryRoot '.tools\rustup'
        $env:PATH = "$taskCargoBin;$env:PATH"
    }

    $taskWinapp = Join-Path $RepositoryRoot '.tools\winapp'
    if (Test-Path -LiteralPath (Join-Path $taskWinapp 'winapp.exe')) {
        $env:PATH = "$taskWinapp;$env:PATH"
        $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
    }

    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw 'Rust is missing. Run scripts\bootstrap.ps1 to install the pinned toolchain inside this workspace.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK is missing. Install the compatible SDK specified in global.json from https://dotnet.microsoft.com/download.'
    }
}

function Invoke-OviChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}
