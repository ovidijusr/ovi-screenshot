# Repository automation

The Windows workflow runs on the explicit `windows-2025` x64 hosted image. It reads .NET selection from `global.json` (including its patch roll-forward policy) and installs workspace-local Rust from `rust-toolchain.toml` through the existing bootstrap script. Action versions are pinned to full upstream commit IDs.

Every push and pull request runs Rust formatting, tests and Clippy, builds the Windows app and smoke programs, and runs the synthetic native Rust bridge check. The workflow has read-only repository permissions and does not publish releases or packages.

Native Media Foundation H.264 encode/decode smoke is an optional **Run workflow** input. Codec availability on hosted images has not been established by a GitHub run. When selected, encoder failures fail the workflow rather than being silently ignored. The smoke uses generated frames and never acquires desktop pixels.

Interactive native UI tests are manual. CI does not establish Windows 11 capture behavior, focus handling, permission behavior, mixed-DPI correctness, clipboard/drag interoperability, physical keyboard mapping, or recording controls. See [development guidance](../docs/development.md) for local checks.

The first hosted run is still required to validate runner/toolchain compatibility. The hosted image receives updates even though the OS label and action commits are explicit.
