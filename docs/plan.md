# Product roadmap

Ovi Screenshot is a native Windows-first capture utility. Windows uses C#/WinUI 3 with thin Win32 adapters; Rust owns portable image processing. A later macOS app will use SwiftUI/AppKit and the same versioned C ABI.

This document separates the current preview from planned work. The [README](../README.md) describes shipped behavior; [development](development.md) covers verification; [release guidance](release.md) covers distribution.

## Current Windows preview

- Notification-area launch, configurable shortcut, and modern theme-aware menus/settings.
- Area capture and Space-to-select-window capture, exported through Rust as PNG.
- Nonactivating stacked previews with hover Copy/Save/Close, double-click open, and native PNG drag-out.
- Selected-area video-only MP4 recording with countdown, pause/resume, stop, and a visible recording boundary.
- Recoverable completed recordings, atomic settings, bounded PNG memory storage, and seven-day inactive cache retention.
- Synthetic Rust, bridge, service, native UI and video checks; a portable Windows x64 packaging script.

The current cache is not an editable or browsable screenshot history. Recording has no audio. The image pipeline is SDR. Screenshot regions can span connected displays. A video stays on the display where its selection starts; cross-monitor video is not implemented.

## Before public distribution

Validate the portable folder on a clean Windows 11 x64 environment. Run intentional manual checks for capture, clipboard/save/drag, physical shortcut mapping, light/dark/high-contrast appearance and relevant display scales. A hosted CI result cannot replace these desktop checks.

Settle the project license and dependency notices. Keep release notes aligned with the tested scope. Signing, an installer and automatic updates require separate distribution work; the first portable preview is unsigned.

## Next: capture reliability and accessibility

Prioritize reproducible user problems over adding surfaces. Exercise mixed-DPI and negative-origin monitors, monitor removal, target-window closure, protected content, output failures and shortcut conflicts. Check keyboard navigation, screen readers, high contrast and theme changes while windows are open.

A complete capture loop means a real selected image reaches Rust and returns through clipboard/save/drag with correct pixels and recoverable errors. Cancellation must remove transient surfaces and preserve existing work.

## Later: editor and local history

Add a native annotation editor only after agreeing on the shared document/command model. Candidate tools are arrows, text, highlighter, solid redaction, crop, background/padding and shadow. One gesture should be one undo step; stale renders must never replace newer revisions.

Keep original pixels immutable. Burn redaction into exported pixels, with a clear distinction between a redacted export and any retained original. Validate output at different zoom levels and on 4K images before committing to a renderer.

Browsable local history needs its own durable schema, recovery, retention/off settings and deletion behavior. A seven-day editable history remains a product target, separate from the current export cache.

## Later: macOS

Build native capture, permission onboarding, shortcut/menu-bar integration, clipboard, save and drag adapters around the existing core. macOS 14+ is a proposed floor, subject to validation. Compile and test on a Mac; Windows tests do not establish macOS permission, focus or capture behavior.

Use the same capture-to-preview workflow with platform-native styling. Do not claim exact parity with CleanShot X or imply affiliation.

## Work organization

For substantial independent work, assign focused core, Windows and UI tasks with explicit file ownership. Agree on shared contracts before dependent edits. Keep small fixes with one owner; avoid duplicate investigation and repeated full-suite runs after the required checks pass. Detailed delegation and validation rules are in [AGENTS.md](../AGENTS.md).

OCR, scrolling capture, cloud sharing, collaboration, image combining, audio recording and ARM64 support remain uncommitted future possibilities.
