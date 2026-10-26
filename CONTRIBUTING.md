# Contributing

Ovi Screenshot is a Windows-first native app with a portable Rust image core. Small, focused changes are easiest to review. For a large feature, open an issue describing the user problem and proposed behavior before starting implementation.

## Build and check

Use Windows 11 x64, the .NET SDK selected by `global.json`, MSVC C++ build tools, and a Windows SDK. [Development instructions](docs/development.md) explain workspace-local Rust setup and the test fixtures.

```powershell
.\scripts\bootstrap.ps1
.\scripts\build.ps1
```

The build checks Rust formatting, tests and Clippy, builds the Windows app, and runs synthetic image and recording checks. The recording check requires the native H.264 encoder. Report a missing codec as a limitation rather than marking that check passed.

For UI changes, verify light/dark themes, keyboard focus and the relevant display scale. Use generated content for automated fixtures. Desktop capture and cross-app clipboard/drag checks require an intentional manual test; do not include personal screenshots in issues or fixtures.

## Keep the boundaries clear

- Put portable image processing in Rust; keep Windows capture, input, windowing and shell integration in the Windows app.
- Keep views focused on presentation and services focused on state and ownership. Prefer clear names and small responsibilities over explanatory comments around complicated code.
- Update the Rust ABI, C header and C# wrapper together. Preserve explicit ownership and validate buffer lengths and layouts.
- Keep product UI compact, accessible and theme-aware. Technical metadata belongs in diagnostics unless it helps the user act.
- Do not commit build outputs, local tooling, credentials, cached captures or machine-specific settings.

Describe the resulting behavior, the reason for the change, and checks actually run. Include UI screenshots only when they help review the change. Be explicit about checks you could not perform. [Architecture](docs/architecture.md) and [project instructions](AGENTS.md) provide further constraints.

License selection is pending; contribution and redistribution terms must be settled before public release.
