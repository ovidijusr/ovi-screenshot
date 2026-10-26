# Windows development

Development uses the .NET SDK selected by `global.json` and a workspace-local Rust toolchain. WinUI package versions are pinned in the app project and NuGet lockfile. Build artifacts stay under ignored directories.

## Prerequisites

- Windows 11 x64.
- Compatible .NET SDK selected by `global.json` (initially 9.0.308).
- MSVC C++ tools and a Windows SDK. Rust needs the native linker even though the core itself is Rust.
- Network access for the first Rust/NuGet dependency restore.

Visual Studio 2022 Build Tools with the Desktop development with C++ workload provides the native linker and Windows SDK. An existing compatible MSVC installation can also be used. No IDE is required to run the scripts.

Install Rust locally when necessary:

```powershell
.\scripts\bootstrap.ps1
```

The bootstrap downloads the official Rust installer and installs the pinned toolchain under `.tools/cargo` and `.tools/rustup`. It does not modify global PATH or user profiles. Build commands select this local installation automatically. Rust versions/components come from `rust-toolchain.toml`.

## Build and verify

```powershell
.\scripts\build.ps1
```

This builds the Rust DLL and Windows app, checks Rust formatting/tests/Clippy, and runs a synthetic C# → Rust PNG smoke test. The smoke test does not capture the desktop, register shortcuts, or use the clipboard. Output goes to `artifacts/windows/Debug`; the test PNG is `core-smoke.png`.

The build also compiles the recording smoke harness. When tests are enabled, it generates an H.264 MP4 from synthetic frames, verifies container/dimensions/duration, decodes a frame, and checks graceful stop of a static-frame provider. These tests never acquire desktop pixels.

Recording pause fixtures use the production `RecordingTimeline`: they verify a frozen paused clock, no paused samples, fresh frames on resume, monotonic timestamps, decoded frames across resume, and a finalized MP4 when stopped while paused. The native UI fixture also completes a real three-second countdown without starting a recorder, cancels a second countdown, checks recording-panel states, and exports `.recording.png`/`.countdown.png` from only its own generated WinUI visual trees for visual review. Actual screen recording remains a user-initiated check.

Use `-Configuration Release` for release-mode artifacts. Use `-SkipTests` only when the required checks have already passed and a local rebuild is sufficient. The build copies the matching Rust DLL into both the app and smoke-test outputs.

## Run

```powershell
.\scripts\run.ps1
```

Launching the app and performing a screenshot are separate actions. Native capture/focus/clipboard/drag behavior must be verified in the real desktop session; compilation and the synthetic bridge smoke do not establish those results.

For quick user testing, double-click `Start Ovi Screenshot.cmd` in the repository root. The app starts in the tray. Single-click its icon for screenshots; right-click and choose Record area for video. Use the tray Quit command to close an older running version before launching the new build.

When an old app is running, the build writes a new versioned output directory rather than overwriting locked files. `current-app.txt` records the newest successfully built executable; the launcher and `run.ps1` read it. Existing instances are not terminated by builds.

Settings and bounded error logs are stored in `%LOCALAPPDATA%\OviScreenshot`. Settings use atomic file replacement and recover to defaults if malformed. PNG payloads spill from a 64 MiB resident preview budget to the shared export cache; live payloads pin their backing files. This is not a browsable history implementation. Recordings use the Recordings cache subdirectory until saved/discarded and completed clips are recovered at startup, with seven-day cache retention. Shutdown cancels unanswered dialogs, drains accepted file writes and active drag operations, and finalizes recording before exiting.

Capture timing and exception messages share one synchronized logger. It keeps current/previous files at no more than 2 MiB each and bounds individual entries. Timing writes run off the overlay's UI path. Recording recovery validates the MP4 itself before accepting cached duration metadata, blocks new recording requests while pending, and observes shutdown cancellation.

The native shell also provides `--ui-smoke <absolute-marker-path>` for bounded native checks. It opens synthetic image previews, checks stack placement/close/hide/restore, verifies negative-origin/crowded layout cases, and reads actual PNG storage-item/bitmap payloads after drag completion is reported. It also instantiates recording controls and the custom icon resource. Tray/hotkey registration and desktop capture are skipped; no data is dropped or sent to another app. A marker is written only after all checks pass, then the app exits.

The service fixtures cover delayed/cancelled recording recovery, late results during shutdown, damaged MP4s with stale metadata, malformed metadata on valid clips, and concurrent/oversized log entries. Native regressions cover re-pressed shortcut learning and callback draining when both event removals fail.

Run this separately from `build.ps1`; it briefly opens app-generated windows:

```powershell
$taskExecutable = (Get-Content .\artifacts\windows\Debug\current-app.txt -Raw).Trim()
$taskMarker = Join-Path (Resolve-Path .\artifacts).Path ('ui-smoke-' + [Guid]::NewGuid().ToString('N') + '.txt')
$taskProcess = Start-Process -FilePath $taskExecutable -ArgumentList @('--ui-smoke', ('"' + $taskMarker + '"')) -WindowStyle Hidden -PassThru
if (-not $taskProcess.WaitForExit(60000)) { throw 'UI smoke did not finish within 60 seconds; inspect its process before retrying.' }
if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskMarker)) { throw 'UI smoke failed; inspect the app log and fixture outputs.' }
Get-Content -LiteralPath $taskMarker
```

The fixture also verifies native key mapping, opens/dismisses the modern tray MenuFlyout, reports its resolved theme, checks memory-only bitmap crops, and shows/cancels a native selection overlay using generated pixels. Key-learning regression checks cover release-only special keys, pre-held modifiers, release order, pending focus loss, cancellation, and unsupported keys; hook installation/disposal is checked synchronously without pumping input. A sibling `.cancel.png` renders normal, hover, and keyboard-focus Cancel states using generated pixels. It does not simulate the user's keyboard or acquire the desktop. Physical Logitech key detection still requires a user test; parsing and generated key events alone cannot establish its signal.

The recording fixture includes four different corner markers and checks their positions after native H.264 encoding/decoding. The reproduced upside-down RGB path was fixed by declaring a positive `MF_MT_DEFAULT_STRIDE` on the uncompressed BGRA media type; do not compensate with PNG rotation or player metadata.

The UI fixture also checks recording-boundary geometry/show/pause/disposal and window-selection geometry/z-order. Window-capture validation uses WGC only against an opaque generated four-color window in this process, then verifies channels, orientation, transparent margins, and PNG export through Rust. No desktop or third-party window pixels are acquired automatically. The `.window.png` artifact includes an app-generated soft shadow; native rounded-corner alpha is preserved when supplied by WGC.

Multi-display checks compose generated memory DIBs with negative origins, unequal resolutions and gaps, then crop/export the combined pixels through Rust. They exercise monitor-local video routing, a generated selector with separate monitor limits, and native monitor metadata, including a same-size origin mismatch. They do not acquire real screen frames. Actual cross-screen dragging, mixed-DPI appearance and monitor reconfiguration during recording require intentional manual checks on the existing display setup.

Previews use the read-only Win32 high-contrast query and native theme notifications; UWP `AccessibilitySettings.HighContrastChanged` could not subscribe in the unpackaged desktop context and is not used.

Some Mac-layout keyboards send Shift+Win+3 from their screenshot key. Native registration conflicts with Windows' taskbar command, so this exact configured chord has a dedicated-thread fallback. The synthetic fixture verifies release/repeat/modifier/pause state and starts/resets/disposes the hook while disabled, leaving input untouched. Production startup checks installation/enabling with the configured binding; actual button-to-selection behavior remains a user test.

If WinApp CLI is present under `.tools/winapp`, build scripts add it to their process PATH and opt out of CLI telemetry. The scripts do not change global PATH. The app references `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` 0.7.1 with private assets.

## Ownership and delegation

Team-sizing rules live in `AGENTS.md`. Rust and native app changes can proceed in parallel after the shared header is agreed. The coordinator owns these scripts and integration; specialists should not modify another owner's files without a handoff.

ABI v2 is specified in `bindings/include/ovi_core.h`. It is hand-maintained with layout/version checks. Change the Rust FFI, header and native wrappers together. Portable shadow composition and alpha-aware thumbnail resampling live in Rust; native capture/color-format adaptation remains in C#.

## Portable packaging

See [Windows preview releases](release.md) for a self-contained Release ZIP, checksum generation, and candidate validation. A normal development build is not the portable distribution.
