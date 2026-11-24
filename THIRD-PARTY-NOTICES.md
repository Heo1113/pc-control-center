# Third-party notices

PC Control Center redistributes the components below in its Windows package (self-contained .NET runtime,
single-file EXEs). Each keeps its own license. The set is the union of what the four integrated programs already
shipped, at the same versions (see `Directory.Packages.props`), plus one component added for the 로컬 AI › Basalt page:
Microsoft.Web.WebView2 (license text below).

## Components shipped in the package

| Component | Version | License | Copyright / project |
|---|---|---|---|
| .NET runtime and WPF (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | 10.0 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Microsoft.Windows.SDK.NET projection, WinRT.Runtime | 10.0.19041 | MIT | Microsoft Corporation — https://github.com/microsoft/CsWinRT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | .NET Foundation and Contributors — https://github.com/CommunityToolkit/dotnet |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider.e_sqlite3, lib.e_sqlite3) | 2.1.x | Apache-2.0 | Zumero LLC / Eric Sink — https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 native library) | bundled with SQLitePCLRaw | Public domain | https://sqlite.org/copyright.html |
| ScottPlot, ScottPlot.WPF | 5.1.59 | MIT | Scott W Harden and contributors — https://github.com/ScottPlot/ScottPlot |
| SkiaSharp, SkiaSharp.Views.WPF and native assets | 3.119.0 | MIT | Microsoft Corporation — https://github.com/mono/SkiaSharp |
| Skia (inside libSkiaSharp) | — | BSD-3-Clause | Google Inc. — https://skia.org |
| HarfBuzzSharp and native assets / HarfBuzz | 8.3.1.1 | MIT / "Old MIT" | https://github.com/mono/SkiaSharp, https://github.com/harfbuzz/harfbuzz |
| OpenTK, OpenTK.GLWpfControl, GLFW (dependencies of ScottPlot.WPF; the OpenGL control is not used) | 4.9.4 / 4.3.3 / 3.4 | MIT / MIT / zlib | https://github.com/opentk/opentk, https://www.glfw.org |
| System.Diagnostics.EventLog | 10.0.14 | MIT | .NET Foundation and Contributors (project C's event log reader) |
| System.Management, System.CodeDom | 10.0.14 | MIT | .NET Foundation and Contributors (project C's WMI snapshots) |
| FlaUI.Core, FlaUI.UIA3, Interop.UIAutomationClient | 5.0.0 / 10.19041.0 | MIT | https://github.com/FlaUI/FlaUI (project E's UI Automation) |
| System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | .NET Foundation and Contributors (project E's DPAPI key storage) |
| Microsoft.Web.WebView2 (Microsoft.Web.WebView2.Core, .Wpf, .WinForms, WebView2Loader.dll) | 1.0.4314.35 | BSD-3-Clause (Microsoft, text below) | Microsoft Corporation — https://aka.ms/webview (Basalt page: shows Basalt's own dashboard at 127.0.0.1) |

The MIT, Apache-2.0, BSD-3-Clause and zlib licenses permit redistribution in binary form with this notice.

### Microsoft.Web.WebView2 1.0.4314.35 — license (LICENSE.txt in the NuGet package, verbatim)

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The package's NOTICE.txt lists Antlr3.Runtime 3.5.2-rc1 and StringTemplate4 4.0.9-rc1 (both BSD-3-Clause, The ANTLR
Project). They are part of the package's `tools/wv2winrt` code generator, which this app does not use; they are not in
the build output or the package.

## Code of the integrated programs (same owner, not third-party)

The source of projects A, B, C and E (all by Heo1113) is imported under `modules/` at pinned commits and compiled into
this app. The projects have no LICENSE files; they belong to the same owner as this repository and the integration
follows the owner's instruction. Their own notices are kept unchanged in `modules/*/THIRD-PARTY-NOTICES.md`; their
"referenced, not copied" lists (Strata's public API, LibreHardwareMonitor evaluated and not used, UFO², UI-TARS
Desktop ideas re-implemented in C#, Microsoft documentation) apply unchanged. See [docs/REUSE.md](docs/REUSE.md).

## Used at run time but not shipped

| Component | How it is used |
|---|---|
| NVIDIA NVML (`nvml.dll`) | Installed by the NVIDIA display driver; loaded from System32 by project A's sensor code, once, by the shared sensor reader. |
| Windows API (kernel32 `GetSystemTimes`, `GlobalMemoryStatusEx`), Windows Event Log, WMI, registry (read-only) | As in projects A and C. Nothing is written. |
| Windows UI Automation | As in project E, only inside the separate Operator host process. |
| Windows PDH (`pdh.dll`), DXGI (`dxgi.dll`), Toolhelp/`OpenProcess`(query-limited)/`GetProcessTimes` (kernel32), `GetExtendedTcpTable` (iphlpapi) | GPU memory page: WDDM GPU memory counters, adapter names, process names and start times, the owner of the Strata port. Loaded from System32; read only. |
| Microsoft Edge WebView2 Runtime (Evergreen) | Renders Basalt's dashboard on the Basalt page. Part of Windows 11 (installed separately on some Windows 10 PCs); not shipped. Without it the page says so and offers the default browser. |
| Basalt (the user's own installation, `C:\AI\basalt` by default) | Started with its own `start-basalt.ps1` in a console window of its own, stopped with Ctrl+C (kernel32 `AttachConsole`/`GenerateConsoleCtrlEvent` in `PcControlCenter.ConsoleSignal.exe`) and, only if it does not end, with `TerminateProcess` on exactly its processes. Its files are only read; nothing of it is shipped. |

## Referenced, not copied

| Project | License | How it was used |
|---|---|---|
| [gputaskman](https://github.com/shanselman/gputaskman) (Scott Hanselman), commit `15ec959` | MIT | Design reference for the GPU memory page: which Windows counters to read (`Local Usage`/`Non Local Usage` before `Dedicated Usage`/`Shared Usage`), the counter instance format, pairing by LUID, listing thresholds and the rule that only measured bytes are summed. No code was copied; the C# implementation was written independently (docs/GPU_MEMORY.md). |
| [Strata](https://github.com/Niko1221/Strata), commit `13eedf5` | MIT | Read to learn which read-only endpoints report memory (none per process) and that the HTTP server starts the engine as a child process. Nothing is called beyond what project A already reads. |

## Test-only dependencies (not shipped)

| Component | License |
|---|---|
| xunit.v3 (and Microsoft.Testing.Platform) | Apache-2.0 / MIT |
