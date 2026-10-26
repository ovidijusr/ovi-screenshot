# Screen and interaction specification

The app is a background utility with capture overlay, floating preview, recording controls and settings. The annotation editor, browsable history, magnifier and additional selection modes below are planned surfaces, not current preview features. Ordinary launch does not open a dashboard. Native tray/menu-bar commands expose the available capture/record modes, Reopen Last Preview, Settings, and Quit; history and additional capture modes follow their implementation milestones.

The shared workflow is confirmed; dimensions below are proposed starting values in logical UI units, not measured CleanShot dimensions. Windows uses effective pixels/DIPs and macOS uses points. Document coordinates and capture dimensions use physical image pixels. These units must never be mixed implicitly.

## Tray and recording workflow

The current preview uses a minimal tray-first experience. Single left click on the capture-frame notification icon opens the shared selector with Screenshot and Video actions. Right click offers Capture area, Record area or Stop recording, Reopen Last Preview, Settings, and Quit. Ordinary startup stays in the tray; settings opens on request or when a startup problem needs attention.

The right-click menu is a native WinUI MenuFlyout and follows the system app theme, including dark mode. A dedicated loaded anchor keeps settings hidden. Settings > Set key detects the physical screenshot key or combination, including Mac keyboard modifiers. Some Mac-layout screenshot buttons send Shift+Win+3. Handle that selected macro directly if Windows rejects its registration because of the taskbar shortcut. Learning pauses the app's existing capture hotkey and selects one chord only while settings is foreground. Accept release-only special keys and modifiers already held at setup. After a chord is selected, allow a brief release interval even if focus changes; never select new keys in another app. Escape, idle focus loss, or timeout ends setup with useful feedback.

Keep visible information brief. Do not show resolution/dimension labels on the capture overlay or screenshot preview. Preserve dimensions internally and for accessible keyboard feedback. The overlay shows the selection, crosshair, and Cancel; the preview shows the image with Copy, Save, and dismiss.

Successful screenshots play a short, quiet capture tick asynchronously. Cancellation, capture failure, preview reopening and video selection stay silent. Do not change the system volume or replace an unavailable sound with a system beep.

The preview also exposes a hover/focus-only Delete icon. Delete removes that capture's automatically saved file and its internal backing payload immediately, without confirmation or a success delay; Reopen Last Preview cannot restore it. Independent Save As, copy and drag exports remain. Disable deletion during an active preview operation, and keep it retriable if deletion fails. Escape and Close continue to dismiss rather than delete.

Screenshots and video use the same selector and toolbar, including Space to pick a window. Show separate screenshot and video actions immediately, with FPS and audio controls in a settings submenu. Keep a completed area outlined, movable and resizable using its edges and corners. Screenshot is available once an area or window is selected; video area selection must fit on one monitor. Record saves the selected preferences and starts a 3–2–1 countdown centered in the selection and clamped to the work area. Escape or Cancel dismisses the selector, toolbar or countdown. Remove transient UI before acquiring recording frames.

A small rounded WinUI panel shows elapsed recorded time, Pause/Resume, and Stop using native theme resources and accessible icon controls. Pausing freezes the displayed time and omits that interval, including audio, from the saved MP4. Resume continues the same file using current screen content. Stop works while paused, finalizes and automatically saves the MP4, and shows its floating preview. If automatic save fails, keep the recording recoverable and offer manual Save/Discard. Recording is SDR.

The WinUI Recording settings section offers 15, 24, 30 and 60 fps (30 by default), separate System sound and Microphone switches (both off by default), and output/input device selectors. Apply recording saves these preferences for the next recording without changing the screenshot shortcut or an active session. Listing devices reads metadata only; capture opens requested devices when recording starts after the countdown. Requested devices that are unavailable produce an error rather than silently removing audio.

Settings uses the WinUI TitleBar control with native caption buttons following Windows' default app mode. The title bar and body share a dynamic theme background, including theme changes while open. Keep the standard drag, minimize/maximize, and close behavior.

While recording, outline the selected physical rectangle with thin click-through strips. Draw them outside the recorded pixels where possible; monitor-edge strips require verified capture exclusion. Keep the outline while paused with a distinct state, and remove it before saving or ending the session. The interior remains unchanged and interactive.

Completed cached recordings are recovered and automatically saved after restart. If automatic save fails, retain the cached recording with Save and an explicit Discard action; Discard asks for confirmation. Closing the panel keeps the recording available. Malformed settings fall back to an active default shortcut and allow rebinding. Error feedback persists until retry or dismissal.

## Capture overlay

For area/display capture, acquire a frozen source image before showing capture controls. Display that image behind the selection overlay so selection is stable even if the underlying app changes. Hide existing preview/editor surfaces where appropriate before acquisition; restore them on cancellation. Window mode may use that frozen desktop for picking feedback, but captures the chosen native window source after hiding controls, rather than cropping the desktop.

| Element | Placement | Behavior |
| --- | --- | --- |
| Selected region | Starts at pointer; grows in any drag direction | Show the selection outline over an unchanged screen image; do not dim outside it |
| Crosshair | At pointer | Fine targeting; visible without depending on color alone |
| Magnifier | About 20 logical units above/right of pointer | Flip and clamp near display edges; show the sampled source pixels |
| Mode strip | Bottom-center of active monitor, about 24 units inside work area | Area, Window, Screen; stay clear of pointer/selection |
| Cancel affordance | In strip; Escape always available | End session, remove overlays, restore prior focus |

The current native Cancel button uses compact rounded theme-aware styling, Segoe UI typography, and a quiet Esc hint. Keep its standard button accessibility and keyboard behavior; do not fall back to the unstyled classic button appearance when optimizing the overlay.

Drag-release defines an area. The user may move or resize it, then use the screenshot or video action on the toolbar. The planning mockup's Preview Capture button only navigates the demonstration.

Disable the selection window's own opening transition and prepare its backing bitmap before revealing it. Still capture uses a one-shot native GDI read; the Win32 overlay consumes its BGRA pixels directly and repaints only selection marks as the pointer moves. Hide previews just before acquisition and synchronize with the compositor only if app surfaces were visible. Do not depend on capture exclusion across GDI drivers or add an arbitrary capture-start delay. Recording continues to use Windows.Graphics.Capture.

During screenshot and video selection, Space toggles window mode. Hover highlights the topmost eligible window, click/Enter selects it, arrows cycle targets, and Space returns to area mode. Keep full native window capture separate from the clipped on-monitor highlight; capture the selected window itself through WGC, even if it spans displays. PNG output preserves available native alpha and adds an app-generated soft shadow on a transparent margin, without a desktop background. Exact native DWM/macOS shadow parity is not promised. Window video follows movement with an updated outline and finishes on close, minimize or resize; it does not change MP4 dimensions mid-recording. Screen mode is a later increment. Do not promise capture of protected or otherwise unavailable windows; show an actionable error.

Screenshot selection spans the connected desktop in physical pixel coordinates. A frozen image is prepared before showing the overlay; monitor gaps are explicitly black. Cancel and the mode hint stay in the initiating monitor's work area, scaled for that monitor. A preview appears on the monitor containing the largest part of the capture. Window picking can target windows on any display.

For video, move the unanchored selector onto any monitor before starting the drag. The first corner latches that monitor; the second corner is clamped to its edge. Convert the final desktop rectangle into that monitor's local physical pixels before starting its WGC recorder. Display changes cancel the selection rather than recording a shifted crop.

Ignore or clearly signal a repeated capture hotkey while a session is active. Monitor removal, lock, sleep, or capture failure must end the overlay session cleanly.

Provide keyboard area selection as an alternative to dragging: start a virtual crosshair at the pointer position, use arrow keys to move by one source pixel and Shift+arrow by ten, press Enter to set an anchor, adjust the opposite corner with arrows, then Enter to capture. Escape cancels at any stage. Tab reaches the mode/cancel controls without changing geometry. Window mode cycles eligible windows with arrows; screen mode cycles displays; Enter captures the highlighted target. Announce selected targets/dimensions through accessibility without flooding announcements on every pointer movement.

## Floating preview

Use a nonactivating native window with the screenshot filling the surface. Fit its aspect ratio into approximately 276 × 180 logical units and constrain it to the display work area. An exceptionally thin image can have a small minimum viewport to keep controls usable. Keep Windows' native rounded frame and shadow. Place the stack 20 logical units inside the capture monitor's bottom-right work-area edge.

There is no header, outer content padding, or permanent action row. Small Copy, Save, Delete and Close icons sit over the image in a lightly translucent top-right overlay. Reveal them on pointer hover or active keyboard focus; pointer focus alone must not leave them pinned after the mouse exits. Video uses a decoded thumbnail with a play badge in the same stack. Give icons accessible names and tooltips, with opaque contrast-theme styling. Keep future Annotate functionality separate until implemented.

Double-clicking the thumbnail opens the saved PNG or MP4 in the default Windows app; Enter on the focused image does the same. If a screenshot could not be saved, open a retained PNG export. Dismiss the preview only after Windows accepts the launch. Opening failure keeps it available with Copy/Save.

Dragging a screenshot supplies a PNG storage item and bitmap; dragging a recording supplies an MP4 storage item. A destination such as a browser or AI attachment window can consume the file. Do not delete the independent export when the preview closes or a drag ends; destination apps may read asynchronously. Keep app-owned drag/open exports for seven days with scoped cleanup.

Copy puts the screenshot bitmap or an independent MP4 file on the clipboard. Save opens a native file dialog for an additional copy; cancel keeps the preview. A successful Copy or Save briefly confirms success then dismisses by default. Failure keeps the capture available and offers retry. Annotate deliberately activates the editor.

Keep each preview until an action or dismissal. New captures create separate windows; earlier captures remain available. Stack variable-height previews upward with a small gap, newest nearest the bottom-right corner. Use additional columns or a layered in-bounds cascade when necessary rather than dropping unsaved captures. Closing one preview compacts the remaining stack. Reopen Last Preview restores the latest available capture without creating duplicates. Durable history remains a later milestone.

Hide every preview before capture and restore the stack after success, cancellation, or error. Avoid starting another capture during an active save or drag operation. Keep window/file lifetimes valid while those operations finish.

Do not give the preview global shortcuts that interfere with the app the user is currently typing into. Its controls become keyboard-accessible after explicit activation.

## Annotation editor

Proposed initial window is 1100 × 760 units, resizable. Target a 900 × 600 minimum where the available work area permits it; otherwise adapt to the display. The central canvas receives most of the space.

| Band | Layout |
| --- | --- |
| Native title bar | Windows caption buttons at right; macOS traffic lights at left; no collisions with product actions |
| Command row | Undo/Redo left, capture title center, Copy and Save right |
| Tool row | Select, Crop, Arrow, Rectangle, Text, Highlighter, Redact; Background toggle toward right |
| Context row | Properties for selected tool/object only: color, width, fill, typography, or crop ratio |
| Main content | Central image canvas; optional right Background inspector, approximately 220–240 units wide |
| Footer | Fit and zoom controls; keep technical metadata out of the default view |

The Background inspector contains fill, padding, corner radius, and shadow controls. Start collapsed for quick annotation in the native app and remember its state. The layout demonstration starts expanded to make placement visible. App controls follow each platform's own typography, spacing, menus, focus, and accessibility conventions.

Initial tools and behavior:

- Select: hit-test, move, resize, and delete editable annotations.
- Crop: non-destructive document crop with explicit apply/cancel.
- Arrow and rectangle: editable geometry, stroke color/width, optional rectangle fill.
- Text: native input/caret/IME behavior feeding shared text layout.
- Highlighter: translucent mark in the shared composition order.
- Redact: solid opaque region baked into Copy/Save output. Do not label ordinary blur as secure redaction.
- Undo/redo: one completed gesture or text edit transaction per step.

On opening, fit the image to the canvas. Source pixels remain the geometry reference at every zoom. Native handles/caret/selection feedback sit above the shared rendered bitmap and do not become exported content.

Keyboard behavior:

| Input | Result |
| --- | --- |
| Ctrl/Cmd+Z and platform redo | Document undo/redo; text field editing remains native while focused |
| Delete/Backspace | Remove selected annotation, except during text input |
| Escape | Cancel current gesture; otherwise clear selection; never silently close and discard the document |
| Space+drag | Pan canvas outside text entry |
| Modified wheel/pinch | Zoom around the interaction point, following platform conventions |
| Unmodified wheel | Scroll/pan rather than unexpected zoom |

Persist editable work to local history when enabled. Closing a successfully persisted document does not show repetitive discard prompts. If history is off or persistence fails and edits have not been preserved, offer a clear save/retry/discard decision. A background render result for an old document revision must never overwrite newer work.

## History

Use a separate native window. Top toolbar has search and Capture; settings is available in native app/menu conventions. Group thumbnail cards by day. Show the capture's title/timestamp and dimensions, without dashboard metrics.

The first history release may search filenames/titles/dates only; do not imply OCR search before OCR exists. Single selection exposes Copy/Save/Annotate; double-click or Enter opens the editor. Delete offers Undo, with later retention cleanup governed by the visible setting.

The confirmed default retention is 7 days, local only. History can be disabled. Explain in setup/settings that editable captures retain original pixels, including pixels obscured in exported redactions. Flattened exported PNGs do not include the editable source or annotation payload.

## Settings and onboarding

Use native settings conventions: macOS Settings window/sidebar and Windows navigation/controls. Categories: General, Shortcuts, Capture, Preview, Editor and Export, History.

Expose the defaults that affect trust and daily use: capture shortcuts/conflicts, preview position/behavior, file destination/format, local history retention/off, and optional launch at sign-in. App name and identity remain provisional. Do not imply launch at sign-in was enabled by install.

Initial setup is a compact explanation of background operation with shortcut assignment and Take Your First Capture. On macOS, request required screen capture access at a relevant capture action and provide denial/revocation recovery. Do not request microphone/camera access for the screenshot scope.

## Failure and recovery states

Capture failure removes overlays and restores prior focus. Hotkey conflict names the failure and offers rebinding. Clipboard, export, and file errors retain the document. Dialog cancellation is not an error. Disk-full history failures make the unsaved state visible. Releasing a preview must not release a document still used by an editor, export, drag, or history transaction.

## Review checklist

The native implementation must be checked with keyboard-only input, screen reader labels, light/dark appearance, mixed display scales, a high-contrast theme, small work areas, and right/left/top-positioned taskbars or Docks. Controls must remain visible and reachable; overlay information must clamp within the active display.

The inline layout preview shows Windows-oriented placement. It demonstrates local screen navigation, editor background controls, history search, and settings placement. It does not simulate desktop capture, pointer tracking, native drag, or real clipboard/file operations. Its small-screen reflow explains the layout in conversation and does not define a mobile product.
