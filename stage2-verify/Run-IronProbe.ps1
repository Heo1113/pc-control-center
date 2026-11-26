<#
  IRON Stage 2 실기 검증 — 실제 읽기 탐침 (읽기 전용, 관리자 권한 불필요)

  IRON을 창 없이 실제 데이터 모드로 N초 실행해 probe.json을 만들고, 사람이 볼 요약(summary.md)을 씁니다.
    1. 소스 상태(NVML, Windows CPU·RAM, WDDM 프로세스 GPU 메모리, 디스크, 프로세스 식별, HWiNFO REST)
    2. 실제 값(GPU 2개, CPU Tctl, RAM, NVMe, WireView 6핀)
    3. 한글 MUI: 파일 설명이 한글로 나온 프로세스 / 영어로만 나온 프로세스
    4. svchost: 서비스 설명(한글)이 역할로 나오는지
    5. WebView2: 호스트 앱이 식별되는지(공유 호스트 표시)
    6. 아이콘: 실제 아이콘을 읽은 프로그램 / 못 읽은 프로그램
    7. 민감정보 자체 점검: 명령줄에 password= 같은 값이 가려지지 않은 채 남았는지

    .\Run-IronProbe.ps1                       # 30초, HWiNFO REST http://127.0.0.1:8764/
    .\Run-IronProbe.ps1 -Seconds 60 -HwInfoUrl off
#>
param(
    [int]$Seconds = 30,
    [string]$HwInfoUrl = 'http://127.0.0.1:8764/',
    [string]$Out = (Join-Path $PSScriptRoot '..\results')
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\IRON\Iron.App.exe'
if (-not (Test-Path $exe)) { throw "Iron.App.exe 없음: $exe" }
$dir = Join-Path $Out ('probe_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Write-Host "IRON 실제 읽기 탐침 $Seconds 초 → $dir"
$p = Start-Process $exe -ArgumentList '--probe-real', $dir, '--probe-seconds', $Seconds, '--hwinfo-url', $HwInfoUrl -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "탐침 종료 코드 $($p.ExitCode) (crash.txt 확인: $dir)" }
$raw = Get-Content (Join-Path $dir 'probe.json') -Raw -Encoding utf8
$j = $raw | ConvertFrom-Json
$ids = @($j.identities)
$hangul = '[가-힣]'
$md = New-Object System.Collections.Generic.List[string]
function L([string]$s) { $md.Add($s); Write-Host $s }

L "# IRON Stage 2 실제 읽기 탐침 — $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
L ""
L "- Windows: $($j.os) · UI 언어 $($j.uiCulture) · 관리자 권한 상승: $($j.elevated) · $($j.seconds)초"
L "- IRON 자체: CPU $($j.iron.cpuPercent)% · Private $($j.iron.privateMb) MB · 핸들 $($j.iron.handles) · 스레드 $($j.iron.threads) · GDI $($j.iron.gdi)"
L ""
L "## 1. 소스 상태"
foreach ($s in $j.sources) { L "- $($s.Name): **$($s.State)**$(if ($s.Detail) { ' — ' + $s.Detail })" }
L ""
L "## 2. 실제 값"
$f = $j.frame
foreach ($g in @($f.gpus)) { L "- GPU $($g.Index) $($g.Name): $($g.TemperatureC) °C · $($g.PowerW) W · 부하 $($g.UtilizationPercent)% · VRAM $($g.VramUsedGb)/$($g.VramTotalGb) GB · 클럭 $($g.ClockMhz) MHz" }
L "- CPU: 부하 $($f.cpu.LoadPercent)% · Tctl $($f.cpu.TctlC) °C (HWiNFO)"
L "- RAM: $($f.ram.UsedGb) / $($f.ram.TotalGb) GB"
L "- NVMe: $($f.ssd.TemperatureC) °C (HWiNFO, 최고값) · 읽기 $($f.ssd.ReadMBps) MB/s (Windows)"
L "- WireView(HWiNFO 경유, IRON은 USB를 열지 않음): 연결 $($f.wireView.Connected) · 핀 $(@($f.wireView.PinCurrentsA) -join ' / ') A · 전력 $($f.wireView.PowerW) W · 전압 $($f.wireView.VoltageV) V"
L "- GPU 사용 프로세스 $(@($f.processes).Count)개, 전체 식별 $($ids.Count)개"
L ""
L "## 3. 한글 MUI (파일 설명)"
$fromFile = @($ids | Where-Object { $_.titleBasis -eq '버전 정보' })
$ko = @($fromFile | Where-Object { $_.cell -match $hangul })
L "- 파일 설명으로 이름을 얻은 프로세스 $($fromFile.Count)개 중 한글 $($ko.Count)개"
foreach ($i in ($ko | Sort-Object image -Unique | Select-Object -First 20)) { L "  - $($i.image) → $($i.cell)" }
L "- 영어로만 나온 Windows 프로세스(한국어 MUI가 없거나 영어 전용):"
foreach ($i in ($fromFile | Where-Object { $_.cell -notmatch $hangul -and $_.company -like '*Microsoft*' } | Sort-Object image -Unique | Select-Object -First 15)) { L "  - $($i.image) → $($i.cell)" }
L ""
L "## 4. svchost 서비스 설명"
foreach ($i in ($ids | Where-Object { $_.image -eq 'svchost.exe' -and $_.role } | Select-Object -First 12)) { L "- $($i.cell): $($i.role)" }
L ""
L "## 5. WebView2"
$wv = @($ids | Where-Object { $_.image -eq 'msedgewebview2.exe' })
if ($wv.Count -eq 0) { L "- 실행 중인 WebView2 없음 — Teams, 위젯, Clipchamp 등 WebView2를 쓰는 앱을 연 뒤 다시 실행하세요." }
foreach ($i in ($wv | Select-Object -First 12)) { L "- $($i.cell) · $($i.executor) · 공유 호스트 $($i.shared) · $(@($i.hosted) -join '; ')" }
L ""
L "## 6. 아이콘"
$withIcon = @($ids | Where-Object { $_.icon })
L "- 실제 아이콘을 읽음: $($withIcon.Count) / $($ids.Count)"
L "- 아이콘을 못 읽은 프로그램(대개 권한 부족인 시스템 프로세스 — 글자 표시로 대신함):"
foreach ($n in ($ids | Where-Object { -not $_.icon } | Select-Object -ExpandProperty image -Unique | Select-Object -First 25)) { L "  - $n" }
L ""
L "## 7. 민감정보 자체 점검"
$leaks = [regex]::Matches($raw, '(?i)(password|passwd|pwd|token|api[-_]?key|secret|access[-_]?key)\s*[=:]\s*[^*\s",\\]{3,}')
L "- 가려지지 않은 비밀값 후보: $($leaks.Count)개$(if ($leaks.Count -gt 0) { ' — 아래 목록을 확인하고 결과를 공유하지 마세요' })"
foreach ($m in ($leaks | Select-Object -First 10)) { L "  - $($m.Value.Substring(0, [Math]::Min(20, $m.Value.Length)))…" }
$md | Set-Content (Join-Path $dir 'summary.md') -Encoding utf8
Write-Host "`n요약: $(Join-Path $dir 'summary.md')"
