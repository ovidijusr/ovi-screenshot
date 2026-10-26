# Screenshot app architecture

Use native capture/windowing and native UI on each platform, with one shared Rust document/edit/export engine. The first slice should prove real pixels travel through Rust before reaching Copy or Save, while keeping heavyweight graphics dependencies out of the initial capture milestone.

## Ownership boundaries

| Responsibility | Owner |
| --- | --- |
| Screen/window enumeration and capture permissions | Native platform adapter |
| Capture acquisition, selection overlays, per-display coordinate transforms | Native platform adapter |
| Hotkeys, tray/menu bar, native clipboard/drag/save dialogs | Native platform adapter |
| Window chrome, toolbars, accessibility, focus, native text input | Native UI |
| Immutable source pixels, image geometry, crop/style, resampling and PNG export | Rust core |
| Annotation commands/rendering (planned) | Rust core |
| Settings, export cache, recording recovery and retention | Windows services today; portable history schema planned |
| Caret, hover/selection handles, transient gesture feedback | Native interaction layer in shared document coordinates |

No local HTTP service, webview, RPC protocol, or network connection is needed for the screenshot scope.

Ordinary Windows capture shortcuts use RegisterHotKey. The Mac-layout screenshot macro Shift+Win+3 conflicts with Windows' taskbar shortcut. If native registration returns ERROR_HOTKEY_ALREADY_REGISTERED for this exact chosen chord, use a dedicated-thread low-level hook that matches only that chord, suppresses its main key, passes modifier state through, and queues capture after release. Pause it during Set key and dispose it on replacement/quit. Do not retain typed keys, inject masking keys, or expand conflict overrides to arbitrary combinations. [Windows shortcuts](https://support.microsoft.com/en-gb/accessibility/windows/keyboard-shortcuts-in-windows), [hook callback guidance](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc).

## Proposed repository layout

```text
apps/windows/          C#/WinUI app and Win32 adapters
apps/macos/            SwiftUI/AppKit app and platform adapters
crates/core/           document, commands, geometry, image/export, history
crates/core-ffi/        C-compatible boundary only
bindings/include/      hand-maintained versioned C header and Swift module map
tests/fixtures/        source images and expected edit/export cases
scripts/               reproducible build, generation, packaging helpers
docs/                  product plan, UI specification, architecture
work/                  reviewable planning mockup
```

The Rust crates, shared C header, Windows app, and build scripts are implemented for the first prototype. The macOS directory and advanced editing/history modules follow in later milestones. Keep native view code separate from capture/service adapters to allow focused agent ownership.

## Windows composition and ownership

`ScreenshotApp` composes services and owns application startup/shutdown. `CaptureCoordinator` owns selection/countdown cancellation and the last capture. `RecordingController` owns recording sessions, elapsed-time updates, the control panel, and serialized Save/Discard actions. `RecordingLibrary` owns retained recording paths/recovery; `SettingsStore` and `ShortcutController` own atomic settings and shortcut updates. Native smoke fixtures live in `Testing`, outside production coordination.

Preview operations use an explicit serialized lifetime with an awaited shutdown boundary. Unanswered interactive dialogs can cancel; an accepted file write or native drag completes before the app exits. Immutable PNG payloads are shared between record copies, with a 64 MiB resident budget for open previews and async spill/readback from the export cache. The budget excludes thumbnails and temporary active read leases.

Private `payload-*.png` backing files are separate from published `drag-*.png` handoff files. Delete reserves the operation gates of every preview sharing the payload, removes its internal backing, marks the shared payload deleted and clears the coordinator's last-capture reference. Failed deletion preserves usable data. Previously saved/exported copies and active read leases remain independent; deletion cannot recall data already handed to another app.

Successful still capture queues a short, original WAV through the native asynchronous sound API after creating its preview. The WAV is bundled in development and published outputs. Audio failure is diagnostic only; it cannot fail the screenshot or substitute a system alert sound.

Recording recovery is part of initialization: selection cannot start a new recording until the scan finishes. Recovery shares one task, observes shutdown cancellation and cannot repopulate a stopped controller. Shutdown drains the scan before disposing its cancellation source.

Thumbnail generation uses alpha-aware area filtering for substantial reductions and bilinear filtering for smaller reductions. Area filtering preserves contributions from thin features that a four-sample thumbnail could miss. Window-shadow blur releases its temporary scratch storage before allocating the output image.

## Native capture pipeline

### Windows

Still screenshots freeze the connected displays into a top-down BGRA DIB. `DisplayTopology` records physical monitor/work-area rectangles and the virtual bounds, including negative origins. `DesktopCapture` clears the canvas to black, copies each real monitor at its translated offset, and checks that the topology is still current. This keeps uneven-layout gaps deterministic. The single-monitor path reuses the existing capture adapter where applicable.

A lightweight Win32 selector spans the frozen virtual desktop. Its bitmap is ready before reveal, and later paints update only crosshair/selection border regions. Controls are positioned and scaled for the initiating monitor; screenshot rectangles remain in physical source pixels across displays. The canvas is bounded to 32,768 pixels per axis and 64 Mi pixels before native allocation. Pixels convert to RGBA only after selection for the Rust pipeline. This is SDR; GDI does not apply color management, so HDR/wide-gamut fidelity needs a separate implementation.

Video recording uses `Windows.Graphics.Capture` with explicit monitor interop for custom source selection. Window screenshots use `CreateForWindow`. [Monitor interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor), [window interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).

The WinUI countdown completes before recording starts. `RecordingTimeline` owns both elapsed recorded time and encoder sample timestamps. Pause stops its clock and blocks sample requests; resume waits for current pixels before restarting the clock. WGC continues draining native frames while paused without CPU readback, allowing a static screen to resume using its current retained frame. Four reusable BGRA buffers hold explicit leases: the timeline owns its latest snapshot and the encoder retains each sample until `Processed`. Abort drains remaining leases only after native consumption ends. Stop wakes paused sample requests and finalizes the same MP4. The WinUI panel reads backend time rather than maintaining a second clock.

Use WinUI 3 for ordinary app windows and a small Win32 layer for hotkeys, tray integration, overlay control, and nonactivating preview behavior. WinUI supports C# and C++; Windows 11 x64 is our initial target choice. [WinUI](https://learn.microsoft.com/windows/apps/WinUI).

Area/display capture flow:

1. Register the configurable shortcut and report conflicts.
2. Hide transient app surfaces and acquire a frozen virtual-desktop frame for the connected displays.
3. Release the temporary GDI capture resources after acquiring the frozen frame.
4. Show the borderless virtual-desktop overlay with controls on the initiating display.
5. Crop the selected source pixel rectangle and normalize its pixel/color representation.
6. Import into Rust, create preview, and export through native output adapters.

Window capture uses a separate path. Space toggles picking from a cached top-first list of eligible windows; resolve the selected HWND, hide selection controls, then acquire it through `CreateForWindow`. Do not substitute a crop of the frozen desktop: that would include occluding content and lose independent window transparency semantics. Convert premultiplied BGRA to straight RGBA without guessing opacity from RGB colors. Preserve the window's available alpha and compose a generated soft shadow on a transparent margin. Do not claim that this is the native DWM or macOS shadow. If the selected handle is recycled, the window disappears/resizes, or its image is unavailable, end the session with a recoverable error.

Acquiring before showing the overlay is the primary self-capture defense. `WDA_EXCLUDEFROMCAPTURE` can protect our own top-level windows in supported capture paths, but is additional protection rather than the only correctness mechanism. [Display affinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity).

Prototype real WGC system capture indicator behavior on packaged and unpackaged apps before promising an invisible capture experience. Handle unsupported/protected targets as explicit errors. Native hotkey registration failure must be actionable. [Capture guidance](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture), [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

### macOS

Use ScreenCaptureKit with `SCShareableContent`, `SCContentFilter`, and `SCScreenshotManager`; use AppKit overlays and native preview panels. At the proposed macOS 14 floor, use the original filter/configuration screenshot methods rather than assuming newer convenience APIs exist. Apple introduced this screenshot capture capability in its 2023 ScreenCaptureKit update. [ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit), [WWDC23](https://developer.apple.com/videos/play/wwdc2023/10136/).

Support screen capture permission check/request, denial/revocation, System Settings recovery, and any required relaunch/retry behavior on target OS versions. Use a stable app identity and signing configuration early for representative permission testing. [Apple capture sample](https://developer.apple.com/documentation/screencapturekit/capturing-screen-content-in-macos), [permission check](https://developer.apple.com/documentation/coregraphics/cgpreflightscreencaptureaccess()).

Native system source pickers and our own selection overlay are separate concepts. ScreenCaptureKit filter support does not remove the need for correct permission onboarding. Microphone/camera permissions are outside scope.

As on Windows, area/display mode crops a frozen display source, while window mode captures the selected window through its ScreenCaptureKit window filter after hiding selection controls. Window source semantics must be validated independently of desktop crop behavior.

## Image and coordinate contract

Each native capture produces a descriptor with width, height, stride, pixel format, alpha mode, byte length, and color-space identification. Validate multiplication/stride overflow, dimensions, length, and allowed formats before copying. Establish a practical resource limit during the capture spike based on display sizes and memory budget.

The shared working representation is 8-bit SDR sRGB RGBA with a documented alpha convention, initially straight/unassociated alpha. Adapters convert native BGRA/premultiplied/wide-gamut/HDR surfaces; the renderer explicitly converts alpha when necessary. Do not reinterpret an HDR or wide-gamut buffer as ordinary sRGB bytes. Windows HDR capture can require float surfaces and deliberate SDR conversion. [Windows capture color guidance](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture).

Document coordinates use a top-left origin in source-image pixels, with floating-point annotation geometry. Crop bounds and final rasterization define exact integer pixel edges. UI points, DPI, canvas zoom, and pan are view transforms, never document coordinates.

Windows uses Per-Monitor V2 awareness. Still-selection source pixels are physical desktop coordinates minus the virtual origin; video source pixels are desktop coordinates minus the chosen monitor origin. `SelectionCoordinates` makes those conversions explicit. DPI affects controls rather than rescaling the captured image, so screens with different effective scales retain their physical pixels. Negative origins and uneven layouts are normal. [DPI guidance](https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows).

## Native screen recording

Selected-area MP4 recording is implemented. On Windows, WGC supplies monitor frames, the native adapter crops the selected pixel region, and Windows `MediaStreamSource`/`MediaTranscoder` encodes H.264 MP4. Recording is video-only and SDR initially, capped at 30 fps. Odd region dimensions receive one padding pixel where the encoder requires even dimensions; content is not stretched.

Stop finalizes and closes the output stream before the Save dialog or copy operation consumes the file. Keep unsaved recordings recoverable in the local cache. The synthetic recording harness tests the same encoder with generated frames and decodes its output without desktop capture. Native recording/permission/desktop behavior requires user-initiated acceptance tests.

Video selection latches one physical monitor and converts the chosen desktop rectangle into that monitor's local coordinates. Its bounds are revalidated around asynchronous recorder startup and on the existing elapsed-time timer. If the monitor is removed, replaced or moved, the controller finalizes the active clip and removes its stale outline instead of continuing with an outdated desktop position.

## Shared C ABI

Use one C-compatible bridge for Swift and C# rather than separate binding architectures. C# uses `LibraryImport` plus `SafeHandle`; Swift can import the shared header/module and wrap handles idiomatically. ABI v2 is hand-maintained and checked against Rust/C# layouts; it includes an independently owned styled document for window shadows. Rust owns portable shadow composition, alpha-aware resampling and bounded PNG output. C is the stable interop target recommended by .NET; a direct C++ ABI is compiler-dependent. [ABI support](https://learn.microsoft.com/dotnet/standard/native-interop/abi-support), [interop practices](https://learn.microsoft.com/dotnet/standard/native-interop/best-practices).

The contract uses opaque document/buffer handles, explicit integer result codes, pointer-plus-length strings/buffers, schema/API versions, and explicit release operations. Never expose Rust collection layouts or managed/native UI objects. UniFFI remains an option to reassess, but C# support is third-party while Swift is first-class, so it is not our default bridge. [UniFFI guide](https://mozilla.github.io/uniffi-rs/latest/).

Provisional first-slice operations:

```text
core_api_version()
document_create_from_pixels(descriptor, borrowed_bytes) -> document
document_get_info(document) -> dimensions, revision
document_render_preview(document, constraints) -> owned_buffer, image_descriptor
document_export_png(document, options) -> owned_buffer
buffer_get_view(buffer) -> borrowed_pointer, length
buffer_release(buffer)
document_release(document)
```

Names are illustrative until the generated C contract is reviewed. Creation copies the input once; it does not retain a borrowed managed pointer. The caller keeps the source pinned/alive only for the duration of that call. Exported buffers remain Rust-owned until explicitly released. Native code must copy or retain a valid buffer owner for every asynchronous clipboard/drag consumer.

Every rendered bitmap includes its actual width, height, stride, pixel format, alpha mode, and color space. A scaled preview cannot be interpreted from the original document dimensions or byte count alone. PNG buffers identify their encoded format rather than pretending to be raw pixel buffers.

Ordinary failures return structured error codes/details. No Rust panic or language exception may unwind across this boundary. Catch recoverable unwinding panics at exported entry points and fail/invalidate affected state; abort-mode panics cannot be caught. [Rust FFI](https://doc.rust-lang.org/stable/nomicon/ffi.html).

## Concurrency and lifecycle

Keep UI work on each native UI thread. Serialize document operations on a wrapper-owned background worker initially. Use document revision/request IDs for render/export responses; discard stale results and coalesce superseded preview requests. Avoid callbacks across the ABI until a concrete need justifies their lifetime complexity.

The first milestone does not need a Rust async runtime. Add explicit cancellation/job handles when operation duration warrants them; do not block the UI waiting for image processing or file writes.

Documents outlive any single preview surface. Track ownership across editor windows, history transactions, exports, and drag sessions. Keep temporary drag files valid while target apps consume them; preview dismissal or drag cancellation must not destroy the only valid image. [Windows drag data](https://learn.microsoft.com/windows/apps/develop/data/drag-and-drop), [AppKit dragging](https://developer.apple.com/documentation/appkit/nsdraggingsession).

## Editing and rendering

Represent a document as immutable source pixels, ordered annotation objects with stable IDs/styles, non-destructive crop/background properties, schema version, and revision. Commands add/update/remove annotations and change crop/presentation. A gesture produces one undo transaction; transient pointer movement does not flood history.

For the editor, use one shared renderer for image content and native controls/selection feedback above its bitmap. Preview/export share compositing, text measurement, and geometry; storing shared annotation data alone does not guarantee identical rendering.

Evaluate a small CPU stack against a Skia-backed alternative using representative 4K inputs. Choose on measured responsiveness, text correctness, memory, binary/build cost, and packaging. Do not adopt a heavyweight GPU/C++ dependency for the first capture loop. Skia is a candidate with meaningful build implications. [Rust Skia](https://github.com/rust-skia/rust-skia).

Render preview at viewport resolution; cache source pixels and avoid full-image clones on each pointer update. Produce full-resolution pixels for export. If annotation text must match exactly across platforms, use the same shaping/layout implementation and a properly licensed bundled font; native app controls can still use OS system fonts.

Solid redaction is flattened into exported pixels. Exported PNGs exclude editable project payload and unnecessary metadata. Editable history may retain original source pixels; this is explicitly explained in UI/settings. Saving an editable project and exporting an image are distinct operations.

## Build and verification

Pin compatible current stable toolchains at bootstrap. Windows requires the selected .NET/WinUI toolchain, Windows SDK, and an MSVC linker environment for Rust. macOS requires Xcode/Swift and matching Rust architecture targets. Compile native shells on their own OS and run shared Rust checks on both.

Meaningful automated cases cover crop boundaries, coordinate transforms, command undo semantics, composition/redaction output, buffer validation/ownership, and deterministic exports. Native smoke checks import known pixels, inspect dimensions, export a PNG, and release resources. Real desktop capture, focus, HDR, permissions, and drag require session/hardware checks from [the delivery plan](plan.md).

## Decisions left for implementation spikes

- Exact supported SDK/.NET versions and packaging mode after toolchain verification.
- Shared renderer/font dependencies after the representative rendering spike.
- Actual WGC indicator/border behavior and permitted handling on the target builds.
- Windows ARM64 and Mac architecture coverage after dependencies and hardware are available.
- Cross-monitor region composition policy if that feature enters scope.

These are contained engineering decisions. The agreed Windows-first capture/preview workflow can proceed without reopening the product direction.
