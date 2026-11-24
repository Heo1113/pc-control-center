# Third-party notices

Windows Diagnostics Lab redistributes the components below in its Windows package (self-contained .NET runtime,
single-file EXEs `WindowsDiagnosticsLab.exe` and `wdl.exe`). Each keeps its own license. Versions are the ones resolved
by `dotnet list package --include-transitive` for this repository.

## Components shipped in the package

| Component | Version | License | Copyright / project |
|---|---|---|---|
| .NET runtime, WPF and Windows Forms (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | 10.0 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms (their THIRD-PARTY-NOTICES apply) |
| Microsoft.Windows.SDK.NET projection, WinRT.Runtime | 10.0.19041 | MIT | Microsoft Corporation — https://github.com/microsoft/CsWinRT |
| System.Diagnostics.EventLog | 10.0.14 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/runtime |
| System.Management, System.CodeDom | 10.0.14 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/runtime |
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

The full license texts are available at the project URLs above. The MIT, Apache-2.0, BSD-3-Clause and zlib licenses
permit redistribution in binary form with this notice.

## Code reused from project A (same owner, not third-party)

Source files from **Local AI Performance Center** (https://github.com/Heo1113/local-ai-performance-center, commit
`5b6aae1`) are reused read-only under `vendor/local-ai-performance-center/` and adapted in a few app files.
Project A has no LICENSE file; it belongs to the same owner as this repository, and the reuse follows the owner's
instruction for this project. Project A states that it contains no source code copied from other projects. Details,
file hashes and the one patch: [docs/REUSE.md](docs/REUSE.md) and `vendor/local-ai-performance-center/manifest.json`.

## Used at run time but not shipped

| Component | How it is used |
|---|---|
| Windows Event Log service | Read-only queries through System.Diagnostics.Eventing.Reader (EvtQuery/EvtRender). Logs are never written, cleared or reconfigured. |
| WMI/CIM (root\cimv2) | Read-only queries: Win32_PhysicalMemory, Win32_VideoController, Win32_DiskDrive, Win32_PnPSignedDriver, Win32_QuickFixEngineering. |
| Windows registry | Read-only: Windows version (`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`) and BIOS/board identification (`HKLM\HARDWARE\DESCRIPTION\System\BIOS`). Nothing is written. |
| NVIDIA NVML (`nvml.dll`) | Installed by the NVIDIA display driver; loaded from System32 by project A's sensor code. Not redistributed. |
| Windows API (kernel32 `GetSystemTimes`, `GlobalMemoryStatusEx`) | Operating system API (project A's sensor code). |

## Referenced, not copied

| Source | How it was used |
|---|---|
| Microsoft documentation: Bug Check Code Reference, Windows Event Log XPath/structured queries, Kernel-Power event 41 troubleshooting, DCOM 10016 guidance, WHEA_ERROR_SOURCE_TYPE | Names and meanings of bug check codes, query syntax, and the cautions shown with events. Written in this project's own words. |
| Publicly described 64-bit kernel dump header layout (DUMP_HEADER64) | Offsets of the signature, bug check code and parameters, dump type and crash time. Only these header fields are read; implausible values are discarded. |

## Test-only dependencies (not shipped)

| Component | License |
|---|---|
| xunit.v3 (and Microsoft.Testing.Platform) | Apache-2.0 / MIT |
