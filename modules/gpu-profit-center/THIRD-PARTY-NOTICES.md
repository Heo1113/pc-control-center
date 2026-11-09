# Third-party notices

GPU Profit & Compute Center redistributes the components below in its Windows package (self-contained .NET runtime,
single-file EXEs `GpuProfitCenter.exe` and `gpc.exe`). Each keeps its own license. Versions are the ones resolved by
`dotnet list src/GpuProfit.App package --include-transitive` for this repository.

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

The full license texts are available at the project URLs above. The MIT, Apache-2.0, BSD-3-Clause and zlib licenses
permit redistribution in binary form with this notice.

## Code reused from projects A and C (same owner, not third-party)

- **Local AI Performance Center** (project A, https://github.com/Heo1113/local-ai-performance-center, commit `5b6aae1`):
  18 source files reused read-only under `vendor/local-ai-performance-center/` (17 unchanged, 1 with project C's
  `filePrefix` patch). Project A has no LICENSE file and states that it contains no source code copied from other projects.
- **Windows Diagnostics Lab** (project C, https://github.com/Heo1113/windows-diagnostics-lab, branch
  `claude/ecstatic-newton-cfosml`, commit `06cc9e5`): storage opening/recovery/backup, the sampling loop, the data
  folder lock, settings storage, WPF services, theme, chart view, smoke test runner and scripts were used as the
  starting point of this app's own files (each file names its source). Project C has no LICENSE file.

Both belong to the same owner as this repository; the reuse follows the owner's instruction for this project.
Details, hashes and the one patch: [docs/REUSE.md](docs/REUSE.md) and `vendor/local-ai-performance-center/manifest.json`.

## Used at run time but not shipped

| Component | How it is used |
|---|---|
| NVIDIA NVML (`nvml.dll`) | Installed by the NVIDIA display driver; loaded from System32 by project A's sensor code, read-only queries (power, energy counter, temperature, utilisation, memory, clocks). Not redistributed. No clock, power-limit or voltage setting is ever written. |
| Windows API (kernel32 `GetSystemTimes`, `GlobalMemoryStatusEx`) | CPU and RAM usage (project A's sensor code). |

## Test-only dependencies (not shipped)

| Component | License |
|---|---|
| xunit.v3 (and Microsoft.Testing.Platform) | Apache-2.0 / MIT |
