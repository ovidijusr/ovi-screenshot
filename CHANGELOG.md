# Changelog

## 0.1.0-preview.1 — Unreleased

First Windows 11 x64 preview. No public release has been published yet.

- Area and window PNG capture with a configurable shortcut and notification-area controls.
- Stacked floating previews with hover actions, clipboard copy, save, double-click open and PNG drag-and-drop.
- Selected-area MP4 recording with countdown, pause/resume, recording outline and upright video output.
- Theme-aware WinUI settings, menus and recording controls.
- Recoverable completed recordings, bounded PNG memory storage and local cache retention.
- Shared Rust image processing through a versioned C ABI, with synthetic bridge and recording checks.
- Portable Windows packaging and repository CI.
- Guarded recording startup/recovery, corrected repeated-key learning, and exception-safe window-capture callback cleanup.
- Shared bounded diagnostic logging, improved thumbnail downscaling, and lower peak shadow-processing memory.
- Soft screenshot confirmation sound and immediate preview deletion, including its internal cached payload.
- Screenshot selections across connected monitors, with negative-origin layouts and per-monitor video selection.

Recording is video-only. macOS, annotations, browsable screenshot history, HDR conversion, signing and an installer are not included.
