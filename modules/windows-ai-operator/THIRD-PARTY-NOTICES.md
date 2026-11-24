# Third-party notices

Windows AI Operator redistributes the following components in its portable package. Each keeps its own license.

| Component | Version | License | Use |
|---|---|---|---|
| .NET runtime, WPF (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | 10.0 | MIT | Runtime and GUI (self-contained EXE) |
| FlaUI.Core, FlaUI.UIA3 (https://github.com/FlaUI/FlaUI) | 5.0.0 | MIT | Windows UI Automation (UIA3) access |
| Interop.UIAutomationClient | 10.19041.0 | MIT (FlaUI dependency) | UIA COM interop |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM view models |
| Microsoft.Data.Sqlite (+ SQLitePCLRaw, SQLite) | 10.0.14 | MIT / Apache-2.0 / public domain | Task history database |
| System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | DPAPI encryption of API keys |

Test-only (not redistributed): xunit.v3 (Apache-2.0).

## Ideas and reference implementations (no code copied)

- Microsoft UFO² (https://github.com/microsoft/UFO, MIT): labelled control lists for the model, control-type filter,
  "confirm sensitive steps", path validation for file access. Re-implemented in C#; no Python code is included.
- UI-TARS Desktop (https://github.com/bytedance/UI-TARS-desktop, Apache-2.0): 0–1000 normalized coordinates relative to a
  screenshot, mapping to physical pixels at execution time. Re-implemented in C#; no TypeScript code is included.
- Strata (https://github.com/Niko1221/Strata, MIT): model list fields that announce image input
  (`architecture.input_modalities`, `/v1/status` `vision.enabled`, llama.cpp `/props` `modalities.vision`), as read by
  Strata's own vision probe. Only the public HTTP API is used.
- The user's own projects A/B (local-ai-performance-center, gpu-profit-center): portable data folder rule, data folder
  lock, dark palette, publish/smoke-test scripts and CI layout (adapted, credited in the files).
