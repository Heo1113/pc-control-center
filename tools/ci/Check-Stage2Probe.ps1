<#
  Checks one probe.json of "Iron.App.exe --probe-real" (Stage 2 real readers) and prints what was read.
    - the secret planted in a test process's command line appears nowhere in the file (only the redacted form)
    - CPU load and RAM are read; with the mock HWiNFO server: CPU Tctl 64.8 °C, NVMe 48 °C, WireView 6 pins (fixture values)
    - processes are identified (the test script is named, its command line redacted)
  Exit code 1 when a check fails.
#>
param(
    [Parameter(Mandatory)] [string]$Probe,
    [string]$Secret,
    [switch]$ExpectHwInfo,
    [string]$Label = 'probe'
)
$ErrorActionPreference = 'Stop'
$raw = Get-Content $Probe -Raw -Encoding utf8
$j = $raw | ConvertFrom-Json
$fails = @()
function Check([string]$name, [bool]$ok, $detail) {
    "  [{0}] {1}{2}" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $name, $(if ($null -ne $detail -and "$detail" -ne '') { " — $detail" } else { '' })
    if (-not $ok) { $script:fails += $name }
}

"== $Label · elevated=$($j.elevated) · UI $($j.uiCulture) · $($j.os) · $($j.seconds) s"
foreach ($s in $j.sources) { "  source: $($s.Name) = $($s.State)$(if ($s.Detail) { ' — ' + $s.Detail })" }
"  IRON itself: CPU $($j.iron.cpuPercent)% · Private $($j.iron.privateMb) MB · WS $($j.iron.workingSetMb) MB · handles $($j.iron.handles) · threads $($j.iron.threads) · GDI $($j.iron.gdi) · USER $($j.iron.user)"
"  loops: hardware $($j.stats.HardwareReads) reads (skipped $($j.stats.HardwareSkipped), last $([math]::Round($j.stats.HardwareLastMs,1)) ms) · processes $($j.stats.ProcessReads) reads (skipped $($j.stats.ProcessSkipped), last $([math]::Round($j.stats.ProcessLastMs,1)) ms)"
if ($j.stats.HwInfo) { "  HWiNFO: requests $($j.stats.HwInfo.Requests) · ok $($j.stats.HwInfo.Ok) · failed $($j.stats.HwInfo.Failed) · skipped $($j.stats.HwInfo.SkippedLate) · SDB loads $($j.stats.HwInfo.SdbLoads)" }
$f = $j.frame
"  frame: phase $($f.phase) · GPUs $(@($f.gpus).Count) · CPU load $($f.cpu.LoadPercent) · Tctl $($f.cpu.TctlC) · RAM $($f.ram.UsedGb)/$($f.ram.TotalGb) GB · SSD $($f.ssd.TemperatureC) °C, read $($f.ssd.ReadMBps) MB/s · WireView $($f.wireView.Connected) pins $(@($f.wireView.PinCurrentsA).Count) · processes on GPU $(@($f.processes).Count)"

if ($Secret) { Check 'secret from the test command line is nowhere in probe.json' (-not $raw.Contains($Secret)) $null }
Check 'CPU load read (Windows counters)' ($null -ne $f.cpu.LoadPercent) $f.cpu.LoadPercent
Check 'RAM read' ($null -ne $f.ram.UsedGb) $f.ram.UsedGb
Check 'NVML state reported' (@($j.sources | Where-Object { $_.Name -like 'NVML*' -and $_.State -in 'Live', 'Unavailable' }).Count -eq 1) ($j.sources | Where-Object { $_.Name -like 'NVML*' } | ForEach-Object State)
if ($ExpectHwInfo) {
    Check 'HWiNFO REST live (mock server)' (@($j.sources | Where-Object { $_.Name -eq 'HWiNFO REST' -and $_.State -eq 'Live' }).Count -eq 1) $null
    Check 'CPU Tctl from HWiNFO = 64.8 (fixture)' ($f.cpu.TctlC -eq 64.8) $f.cpu.TctlC
    Check 'NVMe temperature = 48 (fixture)' ($f.ssd.TemperatureC -eq 48) $f.ssd.TemperatureC
    Check 'WireView 6 pins (fixture)' ($f.wireView.Connected -and @($f.wireView.PinCurrentsA).Count -eq 6) (@($f.wireView.PinCurrentsA) -join ', ')
}
$ids = @($j.identities)
Check 'processes identified' ($ids.Count -ge 30) $ids.Count
$sleeper = @($ids | Where-Object { $_.executor -like '*sleeper.py*' })
if ($Secret) {
    Check 'test script named in its identity' ($sleeper.Count -ge 1) ($sleeper | Select-Object -First 1 | ForEach-Object { $_.cell + ' | ' + $_.executor })
    Check 'its command line kept only redacted' ($sleeper.Count -ge 1 -and $sleeper[0].commandLine -like '*--api-key=`*`*`**') ($sleeper | Select-Object -First 1 | ForEach-Object commandLine)
}
"  identities (sample):"
foreach ($n in 'svchost.exe', 'dllhost.exe', 'cmd.exe', 'powershell.exe', 'pwsh.exe', 'python.exe', 'conhost.exe', 'dotnet.exe', 'explorer.exe', 'csrss.exe', 'RuntimeBroker.exe', 'msedgewebview2.exe') {
    $i = $ids | Where-Object { $_.image -eq $n } | Select-Object -First 1
    if ($i) { "    $($i.image): $($i.cell)$(if ($i.inferred) { ' ~' }) | $($i.detail) | role: $($i.role) | $(@($i.notes) -join '; ') | icon $($i.icon)" }
}
if ($env:GITHUB_STEP_SUMMARY) { "## Stage 2 탐침 ($Label)`n`n실패: $($fails.Count) · 식별 $($ids.Count)개 · IRON CPU $($j.iron.cpuPercent)% · Private $($j.iron.privateMb) MB · 핸들 $($j.iron.handles) · GDI $($j.iron.gdi)" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY }
if ($fails.Count -gt 0) { throw "probe checks failed: $($fails -join '; ')" }
