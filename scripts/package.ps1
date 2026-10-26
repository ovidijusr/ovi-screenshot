[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version = '0.1.0-preview.1'
)

. (Join-Path $PSScriptRoot 'common.ps1')
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Build the Windows portable package on Windows with the documented .NET, Rust, MSVC and Windows SDK prerequisites.'
}
$taskRepository = Get-OviRepositoryRoot
Initialize-OviBuildEnvironment -RepositoryRoot $taskRepository
$taskNumericVersion = ($Version -split '-', 2)[0] + '.0'
$taskPackageName = "OviScreenshot-$Version-win-x64"
$taskReleaseRoot = Join-Path $taskRepository 'artifacts\windows\Release'
$taskRunName = "portable-$Version-win-x64-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$taskOutput = Join-Path $taskReleaseRoot $taskRunName
$taskPublish = Join-Path $taskOutput $taskPackageName
$taskZip = Join-Path $taskOutput "$taskPackageName.zip"
$taskRustDll = Join-Path $taskRepository 'target\x86_64-pc-windows-msvc\release\ovi_core.dll'

[IO.Directory]::CreateDirectory($taskReleaseRoot) | Out-Null
New-Item -ItemType Directory -Path $taskOutput | Out-Null
New-Item -ItemType Directory -Path $taskPublish | Out-Null

Push-Location -LiteralPath $taskRepository
try {
    # Keep the portable Rust DLL independent of a separately installed VC runtime.
    $taskOriginalFlags = [Environment]::GetEnvironmentVariable('RUSTFLAGS', 'Process')
    $taskOriginalEncodedFlags = [Environment]::GetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', 'Process')
    try {
        if ($null -ne $taskOriginalEncodedFlags) {
            $taskSeparator = [char]31
            $env:CARGO_ENCODED_RUSTFLAGS = $taskOriginalEncodedFlags + $taskSeparator + '-C' + $taskSeparator + 'target-feature=+crt-static'
        }
        else {
            $env:RUSTFLAGS = ($taskOriginalFlags + ' -C target-feature=+crt-static').Trim()
        }
        Invoke-OviChecked 'cargo' @('build', '--release', '--locked', '--target', 'x86_64-pc-windows-msvc', '--package', 'ovi-core-ffi')
    }
    finally {
        [Environment]::SetEnvironmentVariable('RUSTFLAGS', $taskOriginalFlags, 'Process')
        [Environment]::SetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', $taskOriginalEncodedFlags, 'Process')
    }

    Invoke-OviChecked 'dotnet' @(
        'publish', 'apps/windows/Ovi.Screenshot.csproj', '--configuration', 'Release',
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $taskPublish,
        '-p:Platform=x64', '-p:RestoreLockedMode=true', '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', "-p:RustCorePath=$taskRustDll",
        "-p:Version=$Version", "-p:AssemblyVersion=$taskNumericVersion",
        "-p:FileVersion=$taskNumericVersion", "-p:InformationalVersion=$Version"
    )

    # The build target copies to OutDir; explicitly include the same DLL in PublishDir.
    Copy-Item -LiteralPath $taskRustDll -Destination (Join-Path $taskPublish 'ovi_core.dll')

    # Unpackaged WinUI publish can omit the app's XAML resource index.
    $taskBuildDirectory = & dotnet msbuild 'apps/windows/Ovi.Screenshot.csproj' '-nologo' '-getProperty:TargetDir' `
        '-p:Configuration=Release' '-p:Platform=x64' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true'
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the matching WinUI build resources.' }
    $taskAppPri = Join-Path (($taskBuildDirectory -join [Environment]::NewLine).Trim()) 'Ovi.Screenshot.pri'
    if (-not (Test-Path -LiteralPath $taskAppPri -PathType Leaf)) {
        throw 'The matching application PRI was not produced. No ZIP was produced.'
    }
    Copy-Item -LiteralPath $taskAppPri -Destination (Join-Path $taskPublish 'Ovi.Screenshot.pri')
    foreach ($taskRequired in @('Ovi.Screenshot.exe', 'Ovi.Screenshot.dll', 'Ovi.Screenshot.runtimeconfig.json',
        'Ovi.Screenshot.pri', 'ovi_core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.ui.xaml.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskPublish $taskRequired) -PathType Leaf)) {
            throw "Portable publish is incomplete: missing $taskRequired. No ZIP was produced."
        }
    }
    if ((Get-FileHash -LiteralPath $taskRustDll -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $taskPublish 'ovi_core.dll') -Algorithm SHA256).Hash) {
        throw 'The published Rust DLL differs from this release build.'
    }
    if ((Get-FileHash -LiteralPath $taskAppPri -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $taskPublish 'Ovi.Screenshot.pri') -Algorithm SHA256).Hash) {
        throw 'The published XAML resource index differs from this release build.'
    }
    $taskUnexpected = Get-ChildItem -LiteralPath $taskPublish -Recurse -File |
        Where-Object { $_.Name -match '^Ovi\.(?:Core|Recording|Service)Smoke(?:\.|$)|^settings\.json$|^app\.log$|^Recording-.*\.mp4$' }
    if ($taskUnexpected) { throw 'The publish directory contains test binaries or user data. No ZIP was produced.' }

    $taskReadme = @"
Ovi Screenshot $Version - Windows 11 x64 preview

Extract the entire ZIP to a writable folder, then run Ovi.Screenshot.exe.
Keep all included files together. The app starts in the notification area.

Click the tray icon or press Ctrl+Shift+4 to select a screenshot area.
Escape cancels. Hover over a preview for Copy, Save and Close.
Right-click the tray icon for recording, Settings, and Quit.
Use Settings > Set key to configure a different capture shortcut.

This is an unsigned preview distributed as a folder, without an installer
or automatic updates. The .NET and Windows App SDK runtimes are bundled.
The app stores preferences and caches in %LOCALAPPDATA%\OviScreenshot;
moving or replacing this program folder does not reset those preferences.
Recording is video-only. macOS and the annotation editor are not included.

Quit the app before replacing it. To remove the program, quit and delete
its extracted folder. User preferences and cached captures remain in AppData.
"@
    [IO.File]::WriteAllText((Join-Path $taskPublish 'README.txt'), $taskReadme, [Text.UTF8Encoding]::new($false))
    foreach ($taskNotice in @('LICENSE', 'LICENSE.txt', 'LICENSE.md', 'NOTICE', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt')) {
        $taskNoticePath = Join-Path $taskRepository $taskNotice
        if (Test-Path -LiteralPath $taskNoticePath -PathType Leaf) {
            Copy-Item -LiteralPath $taskNoticePath -Destination (Join-Path $taskPublish $taskNotice)
        }
    }

    Compress-Archive -LiteralPath $taskPublish -DestinationPath $taskZip -CompressionLevel Optimal
    $taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskChecksum = Join-Path $taskOutput 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($taskChecksum, "$taskHash  $taskPackageName.zip`n", [Text.UTF8Encoding]::new($false))
    $taskReceipt = [ordered]@{
        version = $Version
        runtime = 'win-x64'
        unsigned = $true
        executable = "$taskPackageName/Ovi.Screenshot.exe"
        archive = "$taskPackageName.zip"
        sha256 = $taskHash
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'package.json'), ($taskReceipt | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
}
finally { Pop-Location }

Write-Host "Portable executable: $(Join-Path $taskPublish 'Ovi.Screenshot.exe')"
Write-Host "Archive: $taskZip"
Write-Host "SHA-256: $taskChecksum"
