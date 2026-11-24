# Third-party notices

Local AI Performance Center redistributes the components below in its Windows package
(self-contained .NET runtime, single-file EXE). Each keeps its own license. No source code was
copied from other projects into this repository; the projects in the second table were read for
their public API contracts or as design references only.

## Components shipped in the package

| Component | Version | License | Copyright / project |
|---|---|---|---|
| .NET runtime and WPF (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | 10.0 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/runtime, https://github.com/dotnet/wpf (their THIRD-PARTY-NOTICES apply) |
| Microsoft.Windows.SDK.NET projection, WinRT.Runtime | 10.0.19041 | MIT | Microsoft Corporation — https://github.com/microsoft/CsWinRT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | .NET Foundation and Contributors — https://github.com/CommunityToolkit/dotnet |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider.e_sqlite3, lib.e_sqlite3) | 2.1.13 | Apache-2.0 | Zumero LLC / Eric Sink — https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 native library) | bundled with SQLitePCLRaw | Public domain | https://sqlite.org/copyright.html |
| ScottPlot, ScottPlot.WPF | 5.1.59 | MIT | Scott W Harden and contributors — https://github.com/ScottPlot/ScottPlot |
| SkiaSharp, SkiaSharp.Views.WPF, SkiaSharp.Views.Desktop.Common, SkiaSharp.HarfBuzz, native assets | 3.119.0 | MIT | Microsoft Corporation — https://github.com/mono/SkiaSharp |
| Skia (inside libSkiaSharp) | — | BSD-3-Clause | Google Inc. — https://skia.org |
| HarfBuzzSharp and native assets | 8.3.1.1 | MIT | Microsoft Corporation — https://github.com/mono/SkiaSharp |
| HarfBuzz (inside libHarfBuzzSharp) | — | "Old MIT" | HarfBuzz contributors — https://github.com/harfbuzz/harfbuzz |
| OpenTK (Core, Graphics, Mathematics, Windowing, Input, Compute, Audio.OpenAL) | 4.9.4 | MIT | Team OpenTK — https://github.com/opentk/opentk (dependency of ScottPlot.WPF; this app does not use its OpenGL control) |
| OpenTK.GLWpfControl | 4.3.3 | MIT | varon and contributors — https://github.com/opentk/GLWpfControl |
| GLFW (OpenTK.redist.glfw) | 3.4.0.44 | zlib/libpng | Marcus Geelnard, Camilla Löwy — https://www.glfw.org |

The full license texts are available at the project URLs above. The MIT, Apache-2.0, BSD-3-Clause
and zlib licenses permit redistribution in binary form with this notice.

## Used at run time but not shipped

| Component | How it is used |
|---|---|
| NVIDIA NVML (`nvml.dll`) | Installed by the NVIDIA display driver. Loaded from System32 at run time to read GPU sensors. Not redistributed. |
| Windows API (kernel32 `GetSystemTimes`, `GlobalMemoryStatusEx`) | Operating system API. |

## Referenced, not copied

| Project | License | How it was used |
|---|---|---|
| Strata — https://github.com/Niko1221/Strata | MIT | Read-only HTTP API contract (`docs/TERMINAL_TELEMETRY_FIELDS.md`, `docs/DETAILS.md`, `serve/server.py`, `strata_cli/client.py`). The adapter is a new implementation of that documented contract. Strata is never modified, bundled or started by this app. |
| LibreHardwareMonitor — https://github.com/LibreHardwareMonitor/LibreHardwareMonitor | MPL-2.0 | Evaluated and **not** included: its CPU temperature/power path needs administrator rights and the PawnIO kernel driver, and the library embeds LGPL-2.1 PawnIO modules. GPU and system metrics are read from vendor/OS APIs instead (see README). |
| CapFrameX — https://github.com/CXWorld/CapFrameX | MIT | Design reference only (sensor logging and session comparison concepts). |
| llama.cpp Windows Manager — https://github.com/alekk89/llama-cpp-windows-manager | MIT | Design reference only (project layout, CI publish checks). |
| SysGlance — https://github.com/Frsk-Dev/SysGlance | No license file found | Not used. Without a license no code may be reused. |

## Test-only dependencies (not shipped)

| Component | License |
|---|---|
| xunit.v3 (and Microsoft.Testing.Platform) | Apache-2.0 / MIT |
