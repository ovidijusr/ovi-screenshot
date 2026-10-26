# Microsoft WinUI skill review

Microsoft's WinUI guidance informs native control selection, API verification, accessibility and deployment. This document records the pinned reference and how it applies to this app; the current behavior and validation commands are documented in the README and development guide.

## Source and scope

The requested [microsoft/win-dev-skills repository](https://github.com/microsoft/win-dev-skills) is now a plugin catalog. Its WinUI entry points to `microsoft/winappCli`, path `plugins/winui`, pinned at commit `5a064a427f1e056822a939b13ddce43b798090fa` in the catalog read for this review.

The reference covers eight skills. An optional read-only source copy lives in ignored `.tools/winui-reference`; it is not required to build the app and is not an installed plugin. Follow the pinned upstream links below when that copy is unavailable.

## Skill map

| Skill | Purpose | Relevance here |
| --- | --- | --- |
| `winui-setup` | Detect compatible SDK/CLI/deployment prerequisites | Check before adopting its CLI workflow; avoid redundant toolchain installs |
| `winui-dev-workflow` | Project creation, analyzer, exact-project API checks, builds, launch/crash diagnosis | Relevant to subsequent Windows development |
| `winui-design` | Native control selection, window shape, DPI, themes, accessibility, layout | Primary guidance for the next UI pass |
| `winui-code-review` | Runtime, async, accessibility, performance, bindings, localization | Apply to completed increments; distinguish source risks from reproduced failures |
| `winui-ui-testing` | Scoped UI Automation assertions and visual evidence | Complements the existing core and startup smoke checks |
| `winui-packaging` | MSIX, identity, signing, deployment and AOT distinctions | Relevant when preparing a distributable Windows build |
| `winui-wpf-migration` | Port existing WPF applications | No migration is needed for this WinUI/Rust app |
| `winui-session-report` | Analyze an explicitly requested agent session | Outside this source-review task |

## Guidance to carry into the app

Choose native controls from the intended product shape. The capture flow is a compact utility and the editor is canvas-centered; a permanent navigation sidebar is not necessary for either. Expanded settings can use Windows settings/navigation patterns. Discover samples before inventing controls and verify API availability against the restored app's actual packages. [Design skill](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-design/SKILL.md).

For custom chrome, use semantic theme resources and explicitly review contrast themes. Adapt layouts to available space, use native typography/icons, and provide coherent focus/error/loading states. The screenshot selection canvas is a custom native surface; keyboard and accessibility behavior require focused fixture and manual checks. [Theme and accessibility](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-design/references/theme-accessibility.md), [layout review](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-design/references/layout-review.md).

## Application in this preview

| Area | Current approach | Further validation |
| --- | --- | --- |
| Lifetime and async work | Focused capture/recording services, cancellation, and an awaited shutdown boundary | Interrupt capture, save, drag and recording during intentional manual checks |
| Native chrome | WinUI TitleBar in Settings, theme-aware menus and recording controls | Light/dark/high-contrast changes while open |
| DPI and placement | Physical/logical coordinate boundaries and work-area clamping | Mixed scales, small work areas and display removal |
| Keyboard and accessibility | Named controls, AutomationIds, selection key handling and shortcut learning fixtures | Screen readers, physical keyboard signals and focus restoration |
| Image preparation | Portable styling/resampling in Rust, bounded PNG storage and video frame leases | Large captures and long recordings on representative hardware |

Use the [code-review skill](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-code-review/SKILL.md) and [quality rules](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-code-review/references/quality-rules.md) when reviewing substantive changes. Annotations and browsable history are planned features, not regressions in the current preview.

## Build and test differences

The skill workflow uses WinApp CLI 0.7+ and a per-app `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` reference. These were missing during the initial review. The subsequent tray/recording update added WinApp CLI 0.7.1 locally under `.tools/winapp` and configured WinUIAnalyzer 0.7.1 in the app; the updated build passes with zero warnings/errors. [Development workflow](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-dev-workflow/SKILL.md).

The plugin favors packaged deployment for normal app development. The current `WindowsPackageType=None` build is unpackaged. Portable packaging bundles .NET and Windows App SDK runtimes; MSIX and signing remain separate work. See [release guidance](release.md). `WindowsAppSDKSelfContained=true` bundles the Windows App SDK; it does not by itself bundle the .NET runtime or create a single-file executable. [Packaging skill](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-packaging/SKILL.md).

Existing core tests prove image/ABI behavior. The native UI fixture exercises shell/core/service initialization, generated preview payloads, selection geometry and own-window capture. It skips production hotkey/tray registration, desktop capture and cross-app operations. Continue intentional manual checks for focus restoration, real clipboard/save/drag behavior and errors, with visual review of resulting surfaces. Include 100/150/200% scaling, reverse drag, and light/dark/contrast appearance. A successful build or readable automation tree is insufficient evidence for those behaviors. [UI-testing skill](https://github.com/microsoft/winappCli/blob/5a064a427f1e056822a939b13ddce43b798090fa/plugins/winui/agent-plugin/skills/winui-ui-testing/SKILL.md).

Future testing must use the available authorized automation tools and this project's desktop-capture rules. Parsing these skills did not authorize machine setup, app deployment, certificate trust, or changes to Windows features.
