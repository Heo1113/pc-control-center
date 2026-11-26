<#
  IRON --soak-log CSV 분석: 처음 10분(준비 구간)을 뺀 1분 표본으로 시간당 기울기(최소제곱)를 구합니다.
  누수는 꾸준한 양의 기울기로, 안정 상태는 0 근처로 나타납니다. 기준(참고용, 실측 뒤 조정 가능):
    핸들 ≤ 20/시간 · GDI ≤ 1/시간 · USER ≤ 1/시간 · Private ≤ 5 MB/시간
#>
param([Parameter(Mandatory)] [string]$Csv, [double]$WarmupMinutes = 10)
$rows = Import-Csv $Csv
if ($rows.Count -lt 3) { throw '표본이 3개 미만입니다.' }
$t0 = [DateTimeOffset]::Parse($rows[0].time)
$pts = foreach ($r in $rows) {
    [pscustomobject]@{
        Min = ([DateTimeOffset]::Parse($r.time) - $t0).TotalMinutes
        Private = [double]$r.privateMb; Handles = [double]$r.handles; Gdi = [double]$r.gdi; User = [double]$r.user; Threads = [double]$r.threads
        Cpu = [double]$r.cpuSeconds; Sources = $r.sources
        # newer logs: gen 2 collections and the managed heap the last full GC left (0 = none yet)
        Gen2 = $(if ($r.PSObject.Properties['gen2']) { [double]$r.gen2 } else { -1 })
        HeapGc = $(if ($r.PSObject.Properties['heapAfterFullGcMb']) { [double]$r.heapAfterFullGcMb } else { 0 })
    }
}
$after = @($pts | Where-Object { $_.Min -ge $WarmupMinutes })
function Slope($name, $set = $after) {
    $n = @($set).Count; if ($n -lt 2) { return 0 }
    $mx = ($set | Measure-Object Min -Average).Average; $my = ($set | Measure-Object $name -Average).Average
    $num = 0; $den = 0
    foreach ($p in $set) { $num += ($p.Min - $mx) * ($p.$name - $my); $den += ($p.Min - $mx) * ($p.Min - $mx) }
    if ($den -eq 0) { return 0 } else { return [Math]::Round($num / $den * 60, 2) }
}
# Private alone cannot tell a leak from garbage the GC has not collected yet (CI: the same code once flat, once +147 MB/h
# over 20 min). The managed heap left by each full GC can: it grows only when memory stays referenced.
$gcPts = @($after | Where-Object { $_.HeapGc -gt 0 })
$gcDistinct = @($gcPts | Select-Object -ExpandProperty HeapGc -Unique).Count
$hours = [Math]::Round($pts[-1].Min / 60, 2)
$cpuPct = [Math]::Round(($pts[-1].Cpu - $after[0].Cpu) / (($pts[-1].Min - $after[0].Min) * 60) / [Environment]::ProcessorCount * 100, 3)
$res = [ordered]@{
    시간 = "$hours 시간 ($($pts.Count)개 표본)"
    핸들 = "$($after[0].Handles) → $($pts[-1].Handles) (최대 $(($pts | Measure-Object Handles -Maximum).Maximum)) · 기울기 $(Slope 'Handles')/시간"
    GDI = "$($after[0].Gdi) → $($pts[-1].Gdi) · 기울기 $(Slope 'Gdi')/시간"
    USER = "$($after[0].User) → $($pts[-1].User) · 기울기 $(Slope 'User')/시간"
    스레드 = "$($after[0].Threads) → $($pts[-1].Threads)"
    Private = "$($after[0].Private) → $($pts[-1].Private) MB · 기울기 $(Slope 'Private') MB/시간"
    전체GC후관리힙 = $(if ($pts[0].Gen2 -lt 0) { '기록 없음(이전 버전 로그)' } elseif ($gcPts.Count -lt 2) { "전체 GC 기록 부족(2세대 GC $($pts[-1].Gen2)회)" } else { "$($gcPts[0].HeapGc) → $($gcPts[-1].HeapGc) MB · 기울기 $(Slope 'HeapGc' $gcPts) MB/시간 · 2세대 GC $($pts[-1].Gen2)회" })
    평균CPU = "$cpuPct % (준비 구간 이후)"
    HWiNFO실측아님 = "$(@($pts | Where-Object { $_.Sources -notmatch 'HWiNFO REST: 실측' }).Count)분"
}
$res.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
$flags = @()
if ((Slope 'Handles') -gt 20) { $flags += '핸들 증가 추세' }
if ((Slope 'Gdi') -gt 1) { $flags += 'GDI 증가 추세' }
if ((Slope 'User') -gt 1) { $flags += 'USER 증가 추세' }
if ((Slope 'Private') -gt 5) {
    if ($gcDistinct -ge 3 -and (Slope 'HeapGc' $gcPts) -le 1) { $flags += 'Private 증가(전체 GC 후 관리 힙은 평탄: 아직 수거되지 않은 메모리로 보임)' }
    else { $flags += '메모리 증가 추세' }
}
elseif ($gcDistinct -ge 3 -and (Slope 'HeapGc' $gcPts) -gt 5) { $flags += '전체 GC 후 관리 힙 증가 추세' }
"판정(참고): " + $(if ($flags.Count -eq 0) { '증가 추세 없음' } else { $flags -join ', ' })
