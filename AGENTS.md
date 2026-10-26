# Project instructions

## Product decisions

Build a CleanShot-inspired native screenshot app. Windows 11 comes first; macOS follows later. Use C#/WinUI 3 and thin Win32 adapters on Windows, SwiftUI/AppKit on macOS, and a shared Rust core. Default to a floating preview after capture and seven-day inactive cache retention. A browsable editable history is planned, not implemented. Read `docs/plan.md`, `docs/ui-spec.md`, and `docs/architecture.md` before changing product behavior.

The user wants compact product UI with only useful information. Use a minimal notification-area icon: single left click opens the shared screenshot/video selector, right click offers capture/record/settings/quit. Start in the tray. Do not show resolution or technical metadata in the capture/preview UI by default. Selected-area MP4 recording is confirmed scope; use a small elapsed-time/Stop control rather than a dashboard. Keep technical details in developer documentation and logs.

Screenshots and videos automatically save to Pictures/Ovi Screenshot and share the floating preview stack. A video preview displays a real decoded thumbnail and play badge. Slightly translucent Copy/Save/Delete/Close controls appear on hover or keyboard focus. Delete removes that app-created saved capture and its internal backing immediately, without confirmation; independently saved/shared copies remain. Close only dismisses. Preserve separate captures in a stack rather than replacing the previous preview. Hide and restore the whole stack during capture. Dragging exports a real PNG or MP4 attachment; keep exported files available for asynchronous destination apps.

Successful screenshots play a short, quiet capture tick asynchronously. Cancellation, failure, reopening a preview and video selection stay silent. Never change the system volume or fall back to a system beep when audio is unavailable.

Some Mac-layout keyboard screenshot buttons send Shift+Win+3. Windows reserves this chord for its third taskbar app; support the confirmed screenshot macro through narrowly scoped direct handling if RegisterHotKey rejects it. Do not infer other hardware mappings or override arbitrary conflicting Windows shortcuts. Support unmodified function keys and report other registration conflicts. The tray context menu must use modern theme-aware WinUI controls, not a legacy TrackPopupMenu menu. Capture selection must not dim the screen. Prepare the overlay before reveal, disable its own opening transition, and avoid artificial capture delays. Hide previews just before still acquisition and flush the compositor only when an app surface was visible.

Ordinary app windows must use modern WinUI chrome. Settings uses the WinUI TitleBar control and AppWindowTitleBar.PreferredTheme=UseDefaultAppMode so system caption buttons follow dark/light app mode. Use dynamic theme resources for window surfaces. Keep capture previews and recording controls compact without adding permanent title bars.

The shared WinUI toolbar appears as soon as selection opens and offers Screenshot/Video, with FPS, separate system/microphone switches and device choices in video mode. Keep the selected area visible and draggable before Capture or Record. Record starts the cancellable 3–2–1 countdown, then a compact rounded WinUI panel with elapsed recorded time, Pause/Resume and Stop. Paused time must be omitted from the MP4; Stop must work while paused. Keep backend timing authoritative so the UI timer cannot drift or continue during a pause.

Space toggles window picking for both screenshots and video. Capture the selected HWND through WGC; stills preserve native alpha and add an explicitly generated soft shadow on a transparent PNG canvas. Video follows a fixed-size window when it moves and finalizes on close, minimize or resize. Show a click-through recording boundary (active/paused styling), excluded from capture and removed on Stop/error/quit. Video BGRA samples must declare positive top-down MF_MT_DEFAULT_STRIDE; retain asymmetric decoded orientation regression checks.

WinUI recording settings persist the target FPS (15/24/30/60) and independent system sound/microphone switches and devices. Both audio switches default off and apply to the next recording. Never open audio capture devices just to display settings. WASAPI input is bounded, shares the video clock, and omits paused audio; MP4 includes AAC only when requested. Automated audio checks use generated PCM, never the user's microphone or system playback.

Screenshots can span connected monitors in physical desktop pixels, including negative origins and mixed display scales. Keep monitor gaps black and controls on the initiating display. Video chooses the monitor where the selection starts, clamps its rectangle to that monitor, and converts to monitor-local pixels before WGC/encoding. A single video does not span displays. Cancel safely if the display layout changes.

Double-click a preview (or Enter on its focused image) to open the automatically saved PNG or MP4 with the default app. If screenshot auto-save failed, open a retained PNG export instead. Preserve drag-and-drop, guard duplicate operations, and dismiss only after Windows accepts the launch; failure keeps the preview usable.

Keep production responsibilities in focused services: app composition/lifetime in ScreenshotApp, capture selection in CaptureCoordinator, video state/export in RecordingController, persistence in SettingsStore/RecordingLibrary, and native smoke fixtures in Testing. Preserve explicit ownership and await active operations on shutdown. Portable image composition/resampling belongs in Rust ABI v2. Preview PNG storage is async and disk-backed; avoid hidden synchronous disk reads or unlimited resident payloads. Video frame buffers are bounded leases and cannot be reused before native sample consumption completes.

## Team size and efficiency

The user explicitly wants multiple agents for substantial work, with effort scaled to task size.

- Handle small, focused changes directly with one agent. Do not create a team for a wording fix or a simple local bug.
- For medium work with independent parts, use two focused specialists and coordinator integration.
- For large features, use at most three specialists alongside the coordinator. Add a separate reviewer only when a coherent result is available and the review can address meaningful risk.
- Prefer reusing an existing specialist over spawning duplicate context. Keep inherited model settings unless the user explicitly asks for a model change.
- Give each agent a concrete deliverable, owned files, dependencies, and verification criteria. Avoid agents editing the same files concurrently.
- Agree on shared interfaces before dependent implementation. Parallelize independent work; integrate dependent work in order.
- Keep delegation messages and results concise. Stop redundant exploration and testing after the required checks pass.

The coordinator owns scripts, project documentation, shared contracts approval, integration, and final verification. Core specialists own Rust crates/header; native specialists own their platform directory. Explicitly hand off file ownership before crossing these boundaries.

## Validation and reporting

Prove real capture pixels travel through Rust before calling the capture slice complete. Keep tests focused on geometry, image output, ownership, and real regressions. Compile platform apps on their target OS. Do not report capture/focus/permissions/clipboard/drag behavior as passing based only on compilation or a mockup. Record any unavailable manual checks plainly.

Use workspace-local tooling where practical. Do not change user profiles, global PATH, unrelated software, or system settings to build this project. Do not capture the desktop as part of an automated smoke test; use a synthetic fixture. Native capture is user-initiated or explicitly requested for testing.

## Microsoft WinUI guidance

The user requested parsing Microsoft's WinUI plugin. Its pinned source, skill map, and application review are in `docs/winui-skill-review.md`. Before substantive Windows UI changes, read the relevant linked WinUI design/development/review skill and references; a local read-only copy is under `.tools/winui-reference` when present. Follow compatible platform guidance for DPI, native controls, theme resources, accessibility, and meaningful UI verification. Preserve the working local prototype while planning packaging or toolchain changes explicitly.
