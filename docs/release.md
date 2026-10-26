# Windows preview releases

The initial release target is **0.1.0-preview.1**, an unsigned portable folder for Windows 11 x64. It uses native WinUI controls and the shared Rust image library. The repository contains source and a packaging workflow; these instructions do not establish a public download or published GitHub release.

## Build a portable ZIP

Follow [Windows development](development.md) for the pinned .NET and Rust toolchains, MSVC linker, and Windows SDK. Run the normal verification suite before packaging:

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\package.ps1
```

To package another preview version:

```powershell
.\scripts\package.ps1 -Version 0.1.0-preview.2
```

Each run creates a new directory under `artifacts/windows/Release/portable-<version>-win-x64-<timestamp>-<id>/`. Existing packages and running development builds are not replaced. A failed run leaves its partial output for inspection.

The directory contains:

- `OviScreenshot-<version>-win-x64/`: the published app, Rust DLL, required runtime files, portable instructions, and any root license/notice files present at packaging time.
- `OviScreenshot-<version>-win-x64.zip`: that complete folder, ready to extract.
- `SHA256SUMS.txt`: the ZIP's SHA-256 checksum.
- `package.json`: version, relative executable/archive paths, and checksum.

Packaging builds the x64 release Rust library with a static MSVC runtime and restores its temporary compiler flags afterwards. It passes that release DLL to `dotnet publish` and copies/verifies it in the final output. It also copies/verifies the matching application's `Ovi.Screenshot.pri`; publishing the DLLs without that XAML resource index can prevent WinUI startup. It publishes .NET as self-contained, bundles the Windows App SDK, and leaves trimming and single-file publication disabled. These are separate runtime settings: `WindowsAppSDKSelfContained=true` alone does not bundle .NET. See Microsoft's [.NET publishing guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/) and [unpackaged WinUI deployment guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app).

The package contains the app, not the development launchers, source tree, smoke executables, or user preferences. `package.ps1` builds and packages; it does not launch the app, capture the desktop, sign binaries, create MSIX, install certificates, or publish anything to GitHub. It does not change the development launcher's selected build.

## Use the preview

Extract the entire ZIP and run `Ovi.Screenshot.exe`. Keep the adjacent runtime files and resources together. Startup is in the notification area; there is no main dashboard. Click the tray icon or use the default **Ctrl+Shift+4** shortcut to select an area. Escape cancels selection. Settings lets an existing user profile retain or change its configured shortcut.

Hover over a screenshot preview for Copy, Save, Delete and Close. Right-click the tray icon for recording, Settings, and Quit. Space selects a window for screenshots or video. Recording settings offer target FPS and optional system sound/microphone devices; audio defaults off. The annotation editor, durable browsable screenshot history, and macOS app are not part of this preview.

The preview is unsigned, and Windows may show an unrecognized-publisher or SmartScreen prompt. Do not describe it as signed or verified by a publisher. A self-contained build includes its runtime files; testing on the developer's machine alone does not prove every clean-machine dependency or display scenario.

Preferences and caches remain under `%LOCALAPPDATA%\OviScreenshot`. Updating or moving the extracted folder does not reset them. Quit before replacing the program. Removing the program folder leaves those AppData files intact; the app provides explicit recording save/discard behavior rather than silently removing pending work.

## Check a candidate before public distribution

- Run the Rust, managed bridge, and synthetic recording checks from the documented build workflow. The service checks run inside the separate native `--ui-smoke` fixture.
- Check that the ZIP extracts into one folder and contains `Ovi.Screenshot.exe`, the matching release `ovi_core.dll`, and the .NET/WinUI runtime files. Compare `Get-FileHash -Algorithm SHA256 <archive>` with `SHA256SUMS.txt`.
- Run the bounded native UI fixture against the **published executable**, using a marker inside ignored `artifacts/`. The fixture uses generated content; it must not capture unrelated desktop pixels.
- Test the extracted folder on a clean Windows 11 x64 environment without development tools. Check native DLL dependencies, first tray launch, normal quit, and relaunch. Do not mark this check complete from a self-contained file listing alone.
- Manually check user-initiated screenshot, window capture, recording, clipboard/save/drag, shortcut conflicts, and relevant display scales/themes. Record limitations and unavailable cases.
- Decide the project's distribution license and include its actual text. A license has not been selected by these instructions. Audit required notices for bundled Rust crates, .NET, Windows App SDK, Win2D, and other redistributed dependencies. Copying a root notice file does not substitute for that audit.
- Check the release source/archive for machine-specific settings, paths, credentials, user data, and generated development artifacts. Keep `.tools`, `target`, `artifacts`, `bin`, `obj`, and private fixture outputs out of the public source tree.
- Write release notes matching tested functionality and known limitations. Uploading source or binaries requires a separate explicit publishing instruction.

Code signing, an installer, automatic updates, and MSIX are separate distribution work. No installer or public download URL is claimed here.

Run the published native fixture from the repository root after packaging:

```powershell
$package = Get-ChildItem artifacts/windows/Release -Directory -Filter 'portable-*' |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$receipt = Get-Content (Join-Path $package.FullName 'package.json') -Raw | ConvertFrom-Json
$executable = Join-Path $package.FullName $receipt.executable
$marker = Join-Path $package.FullName ('ui-smoke-' + [Guid]::NewGuid().ToString('N') + '.txt')
$process = Start-Process -FilePath $executable -ArgumentList @('--ui-smoke', ('"{0}"' -f $marker)) `
    -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { throw 'Native fixture timed out; inspect its process before retrying.' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $marker)) { throw 'Native fixture failed.' }
Get-Content -LiteralPath $marker
```

A fresh `PASS` marker and exit code zero are required. This fixture bypasses the normal single-instance guard, so a running normal app can remain open. It exercises service state and generated native UI/image content; it does not establish clean-machine deployment or unrestricted desktop-capture acceptance.
