<#
.SYNOPSIS
    IRON Stage 2 RC3 — HWiNFO REST API 실기 수집기 (읽기 전용). Windows PowerShell 5.1 / PowerShell 7.

.DESCRIPTION
    RC2 Test-HWiNfoRestApi.ps1 을 대체한다. 판정 기준은 모두 ..\config\gates.json 에서 읽는다(스크립트에 숫자를 두지 않음).

    -Mode Inventory : HWiNFO 설치·실행 버전, 8080/8764/8765 점유 프로세스, REST ping, SDB/read 전체 저장,
                      WireView·IRON 관련 프로세스, PnP(VID_0483&PID_5740, VID_2022&PID_0522) 존재 여부.
    -Mode Baseline  : HWiNFO REST 서버를 끈 상태(8764 미점유)에서 HWiNFO64 CPU 사용률 기준선 측정 → baseline.json
    -Mode Collect   : REST 폴링 → run_manifest.json, pings.jsonl, sdb.json, samples_all.jsonl, g1_report.json
                      (-Gate G3A 는 600샘플 + 운영자 확인. G2·G3A 판정은 PoC analyze가 같은 폴더로 수행)
    -Mode Analyze   : 기존 수집 폴더의 G1만 다시 계산 (C# analyze와 같은 정의인지 비교할 때)

    지키는 것
    - GET /api/ping, /api/sensors/sdb, /api/sensors/read 만 보낸다. POST·filter·logging·update_display 없음.
    - 루프백 주소만, 포트 8080(Basalt)은 거부. 어떤 프로세스도 종료·변경하지 않는다.
    - WireView 센서가 SDB에 있고 다른 WireView 클라이언트 프로세스가 실행 중이면 수집하지 않는다(동시 접근 시험 금지).
    - PASS 는 출처가 LIVE_CAPTURE(포트 8764를 연 프로세스가 HWiNFO64로 확인됨, 합성 표시 없음)일 때만.

.EXITCODES
    0 수집 완료·G1 PASS (Inventory/Baseline은 정상 완료) · 1 정책 위반/포트 상태 불일치(무변경 중단) · 2 연결·버전 실패
    3 수집 완료·G1 FAIL · 4 수집 완료·G1 UNVERIFIED · 5 WireView 동시 접근 위험으로 중단

.EXAMPLE
    .\Collect-HWiNfoRest.ps1 -Mode Inventory
    .\Collect-HWiNfoRest.ps1 -Mode Baseline -BaselineSeconds 60
    .\Collect-HWiNfoRest.ps1 -Mode Collect -BaselineFile .\results\baseline_20261125_101500\baseline.json
    .\Collect-HWiNfoRest.ps1 -Mode Collect -Gate G3A -OperatorAttestsHWiNFOExclusive -BaselineFile ...
    .\Collect-HWiNfoRest.ps1 -Mode Analyze -RunDir .\results\run_...
#>
[CmdletBinding()]
param(
    [ValidateSet('Inventory', 'Baseline', 'Collect', 'Analyze')]
    [string]$Mode = 'Inventory',
    [string]$BaseUrl = 'http://127.0.0.1:8764',
    [ValidateSet('G1', 'G3A')]
    [string]$Gate = 'G1',
    [int]$Samples = 0,
    [int]$IntervalMs = 0,
    [string]$OutRoot = '',
    [string]$BaselineFile = '',
    [int]$BaselineSeconds = 60,
    [switch]$OperatorAttestsHWiNFOExclusive,
    [string]$RunDir = '',
    [string]$ConfigDir = '',
    [switch]$SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$Inv = [Globalization.CultureInfo]::InvariantCulture
$Utf8 = New-Object Text.UTF8Encoding $false
$IsWin = ($PSVersionTable.PSEdition -eq 'Desktop') -or ((Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) -and $IsWindows)
if (-not $ConfigDir) { $ConfigDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'config' }
if (-not $OutRoot) { $OutRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'results' }
$AllowedPaths = @('/api/ping', '/api/sensors/sdb', '/api/sensors/read')

# ---------------- JSON (5.1: JavaScriptSerializer, 7: System.Text.Json) ----------------
$script:UseStj = $null -ne ('System.Text.Json.JsonDocument' -as [type])
if (-not $script:UseStj) {
    Add-Type -AssemblyName System.Web.Extensions
    $script:Jss = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $script:Jss.MaxJsonLength = [int]::MaxValue
    $script:Jss.RecursionLimit = 1000
}

function ConvertFrom-JsonText([string]$Text) {
    # 결과: 대소문자를 구분하는 사전/목록/스칼라. (ConvertFrom-Json은 대소문자만 다른 키에서 실패할 수 있어 쓰지 않음)
    if ($script:UseStj) {
        $doc = [System.Text.Json.JsonDocument]::Parse($Text)
        try { return (Convert-Element $doc.RootElement) } finally { $doc.Dispose() }
    }
    return (Convert-Jss ($script:Jss.DeserializeObject($Text)))
}

function Convert-Element($e) {
    switch ($e.ValueKind.ToString()) {
        'Object' {
            $d = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
            foreach ($p in $e.EnumerateObject()) { $d[$p.Name] = Convert-Element $p.Value }
            return , $d
        }
        'Array' { $l = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $e.EnumerateArray()) { $l.Add((Convert-Element $x)) }; return , $l.ToArray() }
        'String' { return $e.GetString() }
        'Number' { return $e.GetDouble() }
        'True' { return $true }
        'False' { return $false }
        default { return $null }
    }
}

function Convert-Jss($o) {
    if ($null -eq $o) { return $null }
    if ($o -is [System.Collections.Generic.IDictionary[string, object]]) {
        $d = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
        foreach ($k in $o.Keys) { $d[$k] = Convert-Jss $o[$k] }
        return , $d
    }
    if ($o -is [object[]] -or $o -is [System.Collections.ArrayList]) { $l = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $o) { $l.Add((Convert-Jss $x)) }; return , $l.ToArray() }
    if ($o -is [int] -or $o -is [long] -or $o -is [decimal] -or $o -is [double]) { return [double]$o }
    return $o
}

# 큰 read/SDB 응답은 최상위 필드만 본다(전체 변환은 PowerShell에서 느림). 객체 값은 '{object}' 표시.
function Get-JsonRoot([string]$Text) {
    $d = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    if ($script:UseStj) {
        $doc = [System.Text.Json.JsonDocument]::Parse($Text)
        try {
            if ($doc.RootElement.ValueKind.ToString() -ne 'Object') { throw 'JSON 최상위가 객체가 아님' }
            foreach ($p in $doc.RootElement.EnumerateObject()) { $d[$p.Name] = Convert-Scalar $p.Value }
        }
        finally { $doc.Dispose() }
        return , $d
    }
    $o = $script:Jss.DeserializeObject($Text)
    if (-not ($o -is [System.Collections.Generic.IDictionary[string, object]])) { throw 'JSON 최상위가 객체가 아님' }
    foreach ($k in $o.Keys) { $v = $o[$k]; if ($v -is [System.Collections.Generic.IDictionary[string, object]]) { $d[$k] = '{object}' } else { $d[$k] = Convert-Jss $v } }
    return , $d
}

function Convert-Scalar($e) {
    switch ($e.ValueKind.ToString()) {
        'Object' { return '{object}' }
        'Array' { return '[array]' }
        'String' { return $e.GetString() }
        'Number' { return $e.GetDouble() }
        'True' { return $true }
        'False' { return $false }
        default { return $null }
    }
}

# samples_all.jsonl 한 줄: 최상위 필드 + read 의 최상위 필드(read 사전은 없으면 $null)
function Get-SampleFields([string]$Line) {
    if ($script:UseStj) {
        $doc = [System.Text.Json.JsonDocument]::Parse($Line)
        try {
            $top = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
            $read = $null
            foreach ($p in $doc.RootElement.EnumerateObject()) {
                if ($p.Name -eq 'read' -and $p.Value.ValueKind.ToString() -eq 'Object') {
                    $read = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
                    foreach ($q in $p.Value.EnumerateObject()) { $read[$q.Name] = Convert-Scalar $q.Value }
                }
                else { $top[$p.Name] = Convert-Scalar $p.Value }
            }
            return [pscustomobject]@{ Top = $top; Read = $read }
        }
        finally { $doc.Dispose() }
    }
    $o = $script:Jss.DeserializeObject($Line)
    $top = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    $read = $null
    foreach ($k in $o.Keys) {
        $v = $o[$k]
        if ($k -eq 'read' -and $v -is [System.Collections.Generic.IDictionary[string, object]]) {
            $read = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
            foreach ($q in $v.Keys) { $x = $v[$q]; if ($x -is [System.Collections.Generic.IDictionary[string, object]]) { $read[$q] = '{object}' } else { $read[$q] = Convert-Jss $x } }
        }
        elseif ($v -is [System.Collections.Generic.IDictionary[string, object]]) { $top[$k] = '{object}' }
        else { $top[$k] = Convert-Jss $v }
    }
    return [pscustomobject]@{ Top = $top; Read = $read }
}

function Get-Num($dict, [string]$key) {
    if ($null -ne $dict -and $dict.ContainsKey($key) -and $dict[$key] -is [double]) { return [double]$dict[$key] }
    return $null
}

function Format-Num($v) {
    if ($null -eq $v) { return 'null' }
    $d = [double]$v
    if ($d -eq [Math]::Floor($d) -and [Math]::Abs($d) -lt 1e15) { return $d.ToString('0', $Inv) }
    return $d.ToString('R', $Inv)
}

function ConvertTo-JsonText($obj) { return ($obj | ConvertTo-Json -Depth 12) }

function Write-Text([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $Utf8) }

# JS Math.round 와 같은 반올림(0.5는 +∞ 쪽) — C# Js.Round 와 같다
function Round-Js($v, [int]$d) {
    if ($null -eq $v) { return $null }
    $x = [double]$v
    if ([double]::IsNaN($x) -or [double]::IsInfinity($x)) { return $null }
    $f = [Math]::Pow(10, $d); $y = $x * $f; $r = [Math]::Floor($y)
    if (($y - $r) -ge 0.5) { $r += 1 }
    return $r / $f
}

# 최근접 순위 백분위수 — C# Gates.Percentile 와 같다
function Get-Percentile([System.Collections.Generic.List[double]]$Values, [double]$P) {
    if ($Values.Count -eq 0) { return $null }
    $a = $Values.ToArray(); [Array]::Sort($a)
    $rank = [int][Math]::Ceiling($P * $a.Length)
    $rank = [Math]::Min([Math]::Max($rank, 1), $a.Length)
    return $a[$rank - 1]
}

# ---------------- 설정·정책 ----------------
$gates = ConvertFrom-JsonText ([IO.File]::ReadAllText((Join-Path $ConfigDir 'gates.json'), [Text.Encoding]::UTF8))
$G1 = $gates['G1']; $G3A = $gates['G3A']; $Fresh = $gates['freshness']; $Ev = $gates['evidence']

function Test-UrlPolicy {
    $u = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$u) -or ($u.Scheme -ne 'http' -and $u.Scheme -ne 'https')) { Write-Host "[중단] 잘못된 URL: $BaseUrl"; exit 1 }
    foreach ($f in $gates['ports']['forbidden']) { if ($u.Port -eq [int]$f) { Write-Host "[중단] 포트 $($u.Port)는 Basalt 운영 포트입니다. 사용하지 않습니다."; exit 1 } }
    if (-not $u.IsLoopback) { Write-Host '[중단] 루프백(127.0.0.1/localhost/::1)만 허용합니다. LAN 접근은 별도 승인 전 금지.'; exit 1 }
    if ($u.Scheme -eq 'https' -and $PSVersionTable.PSEdition -eq 'Desktop') { Write-Host '[중단] Windows PowerShell 5.1에서는 http만 지원합니다(자체 서명 HTTPS는 PowerShell 7 + -SkipCertificateCheck).'; exit 1 }
    return $u
}

# 기준 시각 + Stopwatch: 5.1의 DateTime.UtcNow 해상도(약 15.6 ms)에 RTT가 묶이지 않도록
$script:EpochBase = ([DateTime]::UtcNow - (New-Object DateTime 1970, 1, 1, 0, 0, 0, ([DateTimeKind]::Utc))).TotalMilliseconds
$script:Clock = [Diagnostics.Stopwatch]::StartNew()
function Get-NowMs { return $script:EpochBase + $script:Clock.Elapsed.TotalMilliseconds }

# 요청 시간 제한(초). 준비 단계(ping·SDB)는 5초, 수집 루프는 간격에 맞춰 줄인다(아래 RC3.2 참고).
$script:RequestTimeoutSec = 5
$script:SkippedLate = 'SKIPPED_LATE: 앞 요청이 이 주기를 넘겨 요청하지 않음(따라잡기 없음)'

function Invoke-HwGet([string]$Path) {
    if ($AllowedPaths -notcontains $Path) { throw "허용되지 않은 경로: $Path" }
    $p = @{ Uri = ($BaseUrl.TrimEnd('/') + $Path); Method = 'Get'; TimeoutSec = $script:RequestTimeoutSec; UseBasicParsing = $true }
    if ($SkipCertificateCheck) { $p['SkipCertificateCheck'] = $true }
    $t0 = Get-NowMs
    try {
        $r = Invoke-WebRequest @p
        $t3 = Get-NowMs
        # 응답 바이트를 UTF-8로 직접 해석(5.1이 charset 없는 응답을 ISO-8859-1로 읽어 '°C'가 깨지는 문제 방지)
        $ms = New-Object IO.MemoryStream; $r.RawContentStream.Position = 0; $r.RawContentStream.CopyTo($ms)
        $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
        return [pscustomobject]@{ Ok = ([int]$r.StatusCode -eq 200); Status = [int]$r.StatusCode; Body = $text; T0 = $t0; T3 = $t3; Error = $null }
    }
    catch {
        $t3 = Get-NowMs
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response) { try { $status = [int]$_.Exception.Response.StatusCode } catch { } }
        return [pscustomobject]@{ Ok = $false; Status = $status; Body = $null; T0 = $t0; T3 = $t3; Error = ($_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
    }
}

# ---------------- 증거(읽기 전용) ----------------
function Get-Listener([int]$Port) {
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $rows = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
        $out = @()
        foreach ($r in $rows) {
            $pr = Get-Process -Id $r.OwningProcess -ErrorAction SilentlyContinue
            $path = $null; if ($pr) { try { $path = $pr.Path } catch { } }
            $out += [ordered]@{ port = $Port; address = $r.LocalAddress; pid = [int]$r.OwningProcess; name = $(if ($pr) { $pr.ProcessName } else { $null }); path = $path }
        }
        return , $out
    }
    # Get-NetTCPConnection 이 없는 환경(비 Windows): 연결 가능 여부만, 소유 프로세스는 알 수 없음
    $c = New-Object Net.Sockets.TcpClient
    try {
        $ok = $c.ConnectAsync('127.0.0.1', $Port).Wait(500) -and $c.Connected
        if ($ok) { return , @([ordered]@{ port = $Port; address = '127.0.0.1'; pid = $null; name = $null; path = $null }) }
        return , @()
    }
    catch { return , @() }
    finally { $c.Dispose() }
}

function Get-ProcessesByNames($names) {
    $found = @()
    foreach ($n in $names) {
        foreach ($p in @(Get-Process -Name $n -ErrorAction SilentlyContinue)) {
            $path = $null; try { $path = $p.Path } catch { }
            $found += [ordered]@{ name = $p.ProcessName; pid = $p.Id; path = $path }
        }
    }
    return , $found
}

function Get-FileVersionInfo($path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    $vi = (Get-Item -LiteralPath $path).VersionInfo
    return [ordered]@{ path = $path; fileVersion = $vi.FileVersion; productVersion = $vi.ProductVersion; productName = $vi.ProductName }
}

function Get-HWiNfoInstall {
    $res = [ordered]@{ registry = @(); files = @() }
    if ($IsWin) {
        foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
            foreach ($e in @(Get-ItemProperty -Path $k -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'HWiNFO*' })) {
                $loc = $null; if ($e.PSObject.Properties['InstallLocation']) { $loc = $e.InstallLocation }
                $res.registry += [ordered]@{ displayName = $e.DisplayName; displayVersion = $e.DisplayVersion; installLocation = $loc }
            }
        }
        foreach ($f in @("$env:ProgramFiles\HWiNFO64\HWiNFO64.exe", "${env:ProgramFiles(x86)}\HWiNFO64\HWiNFO64.exe", "$env:ProgramFiles\HWiNFO\HWiNFO64.exe")) {
            $vi = Get-FileVersionInfo $f; if ($vi) { $res.files += $vi }
        }
    }
    return $res
}

function Get-PnpEvidence {
    $out = @()
    if (-not (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue)) { return , $out }
    foreach ($key in $Ev['usbIds'].Keys) {
        $id = $Ev['usbIds'][$key]['pnp']
        foreach ($d in @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like "*$id*" })) {
            $out += [ordered]@{ device = $key; instanceId = $d.InstanceId; friendlyName = $d.FriendlyName; status = $d.Status; class = $d.Class }
        }
    }
    return , $out
}

function Get-ScanProcesses {
    $pat = $Ev['scanPattern']
    $out = @()
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match $pat })) {
        $path = $null; try { $path = $p.Path } catch { }
        $out += [ordered]@{ name = $p.ProcessName; pid = $p.Id; path = $path }
    }
    return , $out
}

function Get-CpuSeconds([int]$ProcessId) {
    try { $p = Get-Process -Id $ProcessId -ErrorAction Stop; return $p.TotalProcessorTime.TotalSeconds } catch { return $null }
}

function Get-HWiNfoProcess {
    $ps = Get-ProcessesByNames $Ev['hwinfoProcessNames']
    if ($ps.Count -gt 0) { return $ps[0] }
    return $null
}

function Test-SdbHasWireView($sdbDict) {
    $re = '(?i)wireview'
    foreach ($k in $sdbDict.Keys) { if ($k -match $re) { return $true } }
    return $false
}

function New-RunDir([string]$prefix) {
    $id = '{0}_{1}_{2}' -f $prefix, (Get-Date -Format 'yyyyMMdd_HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 6))
    $d = Join-Path $OutRoot $id
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    return (Resolve-Path $d).Path
}

# ---------------- G1 (C# Gates.G1 과 같은 정의) ----------------
function Measure-G1([string]$Dir) {
    $manifest = ConvertFrom-JsonText ([IO.File]::ReadAllText((Join-Path $Dir 'run_manifest.json'), [Text.Encoding]::UTF8))
    $source = 'UNKNOWN'; if ($manifest.ContainsKey('source') -and $manifest['source']) { $source = [string]$manifest['source'] }

    # 시계 오프셋: RTT가 가장 짧은 성공 ping(같으면 먼저 기록된 것)
    $offset = $null; $bestRtt = $null
    $pingPath = Join-Path $Dir 'pings.jsonl'
    if (Test-Path -LiteralPath $pingPath) {
        foreach ($line in [IO.File]::ReadAllLines($pingPath, [Text.Encoding]::UTF8)) {
            if (-not $line.Trim()) { continue }
            $p = ConvertFrom-JsonText $line
            if ($p.ContainsKey('ok') -and $p['ok'] -eq $true -and $null -ne (Get-Num $p 't')) {
                $rtt = Get-Num $p 'rttMs'
                if ($null -eq $bestRtt -or $rtt -lt $bestRtt) { $bestRtt = $rtt; $offset = (Get-Num $p 't') - ((Get-Num $p 't0Ms') + (Get-Num $p 't3Ms')) / 2 }
            }
        }
    }

    $n = 0; $success = 0; $versionMismatch = 0; $violations = 0; $stale = 0
    $rtts = New-Object 'System.Collections.Generic.List[double]'
    $readouts = New-Object 'System.Collections.Generic.List[double]'
    $cpuSum = 0.0; $cpuN = 0
    $lastTs = $null; $lastAdvanceAt = $null
    $maxFrozen = [double]$Fresh['maxFrozenMs']; $maxAge = [double]$Fresh['maxAgeMs']
    foreach ($line in [IO.File]::ReadLines((Join-Path $Dir 'samples_all.jsonl'), [Text.Encoding]::UTF8)) {
        if (-not $line.Trim()) { continue }
        $n++
        $f = Get-SampleFields $line
        $s = $f.Top; $read = $f.Read
        $cpu = Get-Num $s 'cpuPct'; if ($null -ne $cpu) { $cpuSum += $cpu; $cpuN++ }
        if (-not ($s.ContainsKey('ok') -and $s['ok'] -eq $true) -or $null -eq $read) { continue }
        if ((Get-Num $read 'Version') -ne 1) { $versionMismatch++; continue }
        $success++
        $rtts.Add((Get-Num $s 'rttMs'))
        $rs = Get-Num $read 'Timestamp RS ms'; $ts = Get-Num $read 'Timestamp ms'; $re = Get-Num $read 'Timestamp RE ms'
        $ordered = ($null -ne $rs) -and ($null -ne $ts) -and ($null -ne $re) -and ($rs -gt 0) -and ($rs -le $ts) -and ($ts -le $re)
        if (-not $ordered -or ($null -ne $lastTs -and $ts -lt $lastTs)) { $violations++; continue }
        $readouts.Add($ts - $rs)
        $t3 = Get-Num $s 't3Ms'
        if ($null -eq $lastTs -or $ts -gt $lastTs) { $lastTs = $ts; $lastAdvanceAt = $t3 }
        $frozen = $t3 - $lastAdvanceAt
        $age = $null; if ($null -ne $offset) { $age = Round-Js ($t3 + $offset - $ts) 0 }
        if ($frozen -gt $maxFrozen -or ($null -ne $age -and $age -gt $maxAge)) { $stale++ }
    }

    $cpuCollect = $null; if ($cpuN -gt 0) { $cpuCollect = $cpuSum / $cpuN }
    $cpuBase = $null
    if ($manifest.ContainsKey('baselineFile') -and $manifest['baselineFile']) {
        $bp = [string]$manifest['baselineFile']; if (-not [IO.Path]::IsPathRooted($bp)) { $bp = Join-Path $Dir $bp }
        if (Test-Path -LiteralPath $bp) { $cpuBase = Get-Num (ConvertFrom-JsonText ([IO.File]::ReadAllText($bp, [Text.Encoding]::UTF8))) 'meanCpuPct' }
    }
    $overhead = $null; if ($null -ne $cpuCollect -and $null -ne $cpuBase) { $overhead = Round-Js ($cpuCollect - $cpuBase) 2 }
    $rate = 0.0; if ($n -gt 0) { $rate = Round-Js ($success * 100.0 / $n) 2 }
    $rttP95 = Round-Js (Get-Percentile $rtts 0.95) 1
    $readP95 = Round-Js (Get-Percentile $readouts 0.95) 1

    $checks = @(
        [ordered]@{ name = 'samples'; value = $n; limit = ('>= ' + (Format-Num $G1['samples'])); pass = ($n -ge $G1['samples']) },
        [ordered]@{ name = 'successRatePct'; value = $rate; limit = ('>= ' + (Format-Num $G1['successRatePctMin'])); pass = ($rate -ge $G1['successRatePctMin']) },
        [ordered]@{ name = 'versionMismatches'; value = $versionMismatch; limit = ('<= 0 (Version ' + (Format-Num $G1['versionRequired']) + ')'); pass = ($versionMismatch -eq 0) },
        [ordered]@{ name = 'rttP95Ms'; value = $rttP95; limit = ('<= ' + (Format-Num $G1['rttP95MsMax'])); pass = (($null -ne $rttP95) -and ($rttP95 -le $G1['rttP95MsMax'])) },
        [ordered]@{ name = 'readoutP95Ms'; value = $readP95; limit = ('<= ' + (Format-Num $G1['readoutP95MsMax'])); pass = (($null -ne $readP95) -and ($readP95 -le $G1['readoutP95MsMax'])) },
        [ordered]@{ name = 'timestampViolations'; value = $violations; limit = ('<= ' + (Format-Num $G1['timestampViolationsMax'])); pass = ($violations -le $G1['timestampViolationsMax']) }
    )
    $staleCheck = [ordered]@{ name = 'staleSamples'; value = $stale; limit = ('<= ' + (Format-Num $G1['staleSamplesMax'])); pass = ($stale -le $G1['staleSamplesMax']) }
    if ($null -eq $offset) { $staleCheck['note'] = 'ping 시계 오프셋 없음 — 정지 시간만으로 판정' }
    $cpuCheck = [ordered]@{ name = 'hwinfoCpuOverheadPct'; value = $overhead; limit = ('<= ' + (Format-Num $G1['hwinfoCpuOverheadPctMax'])); pass = $null }
    if ($null -ne $overhead) { $cpuCheck['pass'] = ($overhead -le $G1['hwinfoCpuOverheadPctMax']) }
    elseif ($null -eq $cpuBase) { $cpuCheck['note'] = 'REST 비활성 기준선(baseline.json) 없음' }
    else { $cpuCheck['note'] = '수집 중 HWiNFO CPU 표본 없음' }
    $checks += $staleCheck; $checks += $cpuCheck

    $anyFalse = @($checks | Where-Object { $_['pass'] -eq $false }).Count -gt 0
    $anyNull = @($checks | Where-Object { $null -eq $_['pass'] }).Count -gt 0
    $met = $true; if ($anyFalse) { $met = $false } elseif ($anyNull) { $met = $null }
    $verdict = 'PASS'
    if ($source -ne 'LIVE_CAPTURE') { $verdict = 'UNVERIFIED' } elseif ($met -eq $false) { $verdict = 'FAIL' } elseif ($null -eq $met) { $verdict = 'UNVERIFIED' }
    return [ordered]@{
        gate = 'G1'; source = $source; criteriaMet = $met; verdict = $verdict
        clockOffsetMs = (Round-Js $offset 1)
        cpu = [ordered]@{ collectMeanPct = (Round-Js $cpuCollect 2); baselineMeanPct = (Round-Js $cpuBase 2) }
        checks = $checks
    }
}

function Show-G1($g1, [string]$Dir) {
    Write-Host ''
    Write-Host ("G1 {0} (출처 {1}, 기준 충족 {2})" -f $g1.verdict, $g1.source, $g1.criteriaMet)
    foreach ($c in $g1.checks) {
        $res = '판정 불가'; if ($c['pass'] -eq $true) { $res = '충족' } elseif ($c['pass'] -eq $false) { $res = '미달' }
        $note = ''; if ($c.Contains('note')) { $note = ' — ' + $c['note'] }
        Write-Host ("  {0,-22} {1,10}  {2,-20} {3}{4}" -f $c['name'], (Format-Num $c['value']), $c['limit'], $res, $note)
    }
    Write-Host "보고서: $(Join-Path $Dir 'g1_report.json')"
    Write-Host "G2·G3A 판정: dotnet run --project <RC3>\poc -c Release -- analyze `"$Dir`""
}

function Get-ExitCode($g1) {
    switch ($g1.verdict) { 'PASS' { return 0 } 'FAIL' { return 3 } default { return 4 } }
}

# ---------------- 모드 ----------------
if ($Mode -eq 'Analyze') {
    if (-not $RunDir) { Write-Host '-RunDir 가 필요합니다.'; exit 1 }
    $g1 = Measure-G1 $RunDir
    Write-Text (Join-Path $RunDir 'g1_report.json') (ConvertTo-JsonText $g1)
    Show-G1 $g1 $RunDir
    exit (Get-ExitCode $g1)
}

$u = Test-UrlPolicy
$hwPort = [int]$gates['ports']['hwinfo']; $relayPort = [int]$gates['ports']['relay']; $basaltPort = [int]$gates['ports']['basalt']

if ($Mode -eq 'Inventory') {
    $dir = New-RunDir 'inventory'
    $ports = [ordered]@{}
    foreach ($p in @($basaltPort, $hwPort, $relayPort, $u.Port) | Select-Object -Unique) { $ports["$p"] = Get-Listener $p }
    $hw = Get-HWiNfoProcess
    $hwVer = $null; if ($hw) { $hwVer = Get-FileVersionInfo $hw.path }
    $inv = [ordered]@{
        schema = 'iron.rc3.inventory/1'; createdAtLocal = (Get-Date).ToString('o'); powershell = $PSVersionTable.PSVersion.ToString(); edition = $PSVersionTable.PSEdition
        hwinfo = [ordered]@{ process = $hw; runningVersion = $hwVer; install = (Get-HWiNfoInstall); note = 'REST API 지원 여부는 /api/ping 응답으로 판단(공식 문서에 최소 버전 명시 없음)' }
        ports = $ports
        rest = $null
        processes = [ordered]@{ hwinfo = (Get-ProcessesByNames $Ev['hwinfoProcessNames']); wireViewClients = (Get-ProcessesByNames $Ev['wireViewClientProcessNames']); scan = (Get-ScanProcesses) }
        pnp = (Get-PnpEvidence)
        notes = @('읽기 전용: 프로세스 종료·설정 변경 없음', 'WireView 클라이언트 프로세스 이름은 추정값 — scan 목록으로 실제 이름을 확인해 config/gates.json evidence.wireViewClientProcessNames 를 고칠 것')
    }
    $ping = Invoke-HwGet '/api/ping'
    $rest = [ordered]@{ url = $BaseUrl; pingOk = $false; pingStatus = $ping.Status; rttMs = (Round-Js ($ping.T3 - $ping.T0) 1); error = $ping.Error; sdbSaved = $false; readSaved = $false; sdbReadings = $null; wireViewInSdb = $null }
    if ($ping.Ok) {
        try { $pj = ConvertFrom-JsonText $ping.Body; $rest.pingOk = ($pj.ContainsKey('pong') -and $pj['pong'] -eq $true); $rest['serverT'] = Get-Num $pj 't' } catch { $rest.error = $_.Exception.Message }
    }
    if ($rest.pingOk) {
        $sdb = Invoke-HwGet '/api/sensors/sdb'
        if ($sdb.Ok) {
            Write-Text (Join-Path $dir 'sdb.json') $sdb.Body; $rest.sdbSaved = $true
            $sd = Get-JsonRoot $sdb.Body
            $rest.sdbVersion = Get-Num $sd 'Version'
            $rest.wireViewInSdb = Test-SdbHasWireView $sd
        }
        $rd = Invoke-HwGet '/api/sensors/read'
        if ($rd.Ok) { Write-Text (Join-Path $dir 'read_once.json') $rd.Body; $rest.readSaved = $true; $rest.readVersion = Get-Num (Get-JsonRoot $rd.Body) 'Version' }
    }
    $inv.rest = $rest
    Write-Text (Join-Path $dir 'inventory.json') (ConvertTo-JsonText $inv)
    Write-Host "[Inventory] $dir"
    Write-Host ("  HWiNFO 실행: {0}  버전: {1}" -f $(if ($hw) { "$($hw.name) PID $($hw.pid)" } else { '없음' }), $(if ($hwVer) { $hwVer.productVersion } else { '확인 불가(경로 접근 불가 시 관리자 PowerShell 필요)' }))
    foreach ($k in $ports.Keys) {
        $l = $ports[$k]; $txt = '미점유'
        if ($l.Count -gt 0) { $txt = ($l | ForEach-Object { "{0} PID {1}" -f $(if ($_.name) { $_.name } else { '소유자 미확인' }), $_.pid }) -join ', ' }
        Write-Host ("  포트 {0}: {1}" -f $k, $txt)
    }
    Write-Host ("  REST ping: {0}  SDB 저장: {1}  read 저장: {2}  WireView 센서(SDB): {3}" -f $rest.pingOk, $rest.sdbSaved, $rest.readSaved, $rest.wireViewInSdb)
    Write-Host ("  WireView 클라이언트 프로세스(추정 이름): {0}" -f (@($inv.processes.wireViewClients | ForEach-Object { $_.name }) -join ', '))
    Write-Host ("  PnP: {0}" -f (@($inv.pnp | ForEach-Object { "$($_.device) $($_.status)" }) -join ', '))
    Write-Host "  전체 센서 인벤토리 표: dotnet run --project <RC3>\poc -c Release -- analyze 는 Collect 폴더에서 sensor_inventory.csv 를 만듭니다."
    exit 0
}

if ($Mode -eq 'Baseline') {
    $l = Get-Listener $hwPort
    if ($l.Count -gt 0) { Write-Host "[중단] 포트 $hwPort 가 열려 있습니다. HWiNFO 설정에서 REST 서버를 끈 뒤(HWiNFO 자체는 켠 상태) 다시 실행하세요."; exit 1 }
    $hw = Get-HWiNfoProcess
    if (-not $hw) { Write-Host '[중단] HWiNFO64 프로세스가 없습니다. 평소 설정(센서 창·폴링 주기 동일)으로 실행한 뒤 측정하세요.'; exit 1 }
    $dir = New-RunDir 'baseline'
    $lp = [Environment]::ProcessorCount
    $cpuSamples = @(); $prevCpu = Get-CpuSeconds $hw.pid; $prevT = Get-NowMs
    if ($null -eq $prevCpu) { Write-Host '[중단] HWiNFO64의 CPU 시간을 읽을 수 없습니다(권한). 관리자 PowerShell에서 다시 실행하세요.'; exit 1 }
    for ($i = 1; $i -le $BaselineSeconds; $i++) {
        Start-Sleep -Milliseconds 1000
        $cpu = Get-CpuSeconds $hw.pid; $t = Get-NowMs
        if ($null -eq $cpu) { Write-Host '[중단] HWiNFO64가 종료되었습니다.'; exit 1 }
        $pct = ($cpu - $prevCpu) * 1000.0 / ($t - $prevT) / $lp * 100.0
        $cpuSamples += [ordered]@{ tMs = $t; cpuPct = $pct }
        $prevCpu = $cpu; $prevT = $t
        $lnow = Get-Listener $hwPort  # 함수가 배열 하나를 내보내므로 @()로 감싸면 빈 배열도 1개로 세어진다
        if ($lnow.Count -gt 0) { Write-Host '[중단] 측정 중 REST 서버가 켜졌습니다. 기준선이 무효입니다.'; exit 1 }
    }
    $mean = ($cpuSamples | ForEach-Object { $_.cpuPct } | Measure-Object -Average).Average
    $b = [ordered]@{ schema = 'iron.rc3.baseline/1'; mode = 'Baseline'; restListening = $false; hwinfoProcess = $hw; hwinfoVersion = (Get-FileVersionInfo $hw.path); logicalProcessors = $lp; durationS = $BaselineSeconds; cpuUnit = '전체 논리 프로세서 대비 %'; samples = $cpuSamples; meanCpuPct = $mean }
    Write-Text (Join-Path $dir 'baseline.json') (ConvertTo-JsonText $b)
    Write-Host ("[Baseline] REST 비활성 HWiNFO64 평균 CPU {0}% (논리 프로세서 {1}개 기준) → {2}" -f (Round-Js $mean 3), $lp, (Join-Path $dir 'baseline.json'))
    exit 0
}

# ---------------- Collect ----------------
if ($Samples -le 0) { $Samples = [int]$gates[$Gate]['samples'] }
if ($IntervalMs -le 0) { $IntervalMs = [int]$gates[$Gate]['intervalMs'] }
if ($IntervalMs -lt [int]$gates['hwinfo']['minPollMs']) { Write-Host "[중단] 폴링 간격은 $($gates['hwinfo']['minPollMs']) ms 이상이어야 합니다."; exit 1 }
if ($Gate -eq 'G3A' -and -not $OperatorAttestsHWiNFOExclusive) { Write-Host '[중단] G3A는 운영자 확인이 필요합니다: WireView 제조사 앱·iUnity 등 다른 WireView 클라이언트를 직접 종료하고 HWiNFO만 WireView에 접근하는 상태에서 -OperatorAttestsHWiNFOExclusive 를 붙여 실행하세요.'; exit 1 }

if ($BaselineFile -and -not (Test-Path -LiteralPath $BaselineFile -PathType Leaf)) { Write-Host "[중단] 기준선 파일 없음: $BaselineFile"; exit 1 }
$listen = Get-Listener $u.Port
if ($listen.Count -eq 0) { Write-Host "[중단] $($u.Port) 포트에 응답하는 서버가 없습니다. HWiNFO 설정에서 REST 서버를 켜세요."; exit 2 }
$owner = $listen[0]
$hwNames = @($Ev['hwinfoProcessNames'])
$source = 'UNVERIFIED_ENDPOINT'; $sourceReason = "포트 $($u.Port) 소유 프로세스를 확인할 수 없음(Get-NetTCPConnection 없음)"
if ($null -ne $owner.pid) {
    if ($hwNames -contains $owner.name) { $source = 'LIVE_CAPTURE'; $sourceReason = "포트 $($u.Port) 소유 프로세스 $($owner.name) PID $($owner.pid)" }
    else { Write-Host "[중단] 포트 $($u.Port)를 HWiNFO가 아닌 프로세스($($owner.name) PID $($owner.pid))가 열고 있습니다. 아무것도 바꾸지 않고 종료합니다."; exit 1 }
}

$wvClients = Get-ProcessesByNames $Ev['wireViewClientProcessNames']
$dir = New-RunDir ('run_' + $Gate)
$pingLines = New-Object Text.StringBuilder
$pingOk = 0
for ($k = 0; $k -lt 5; $k++) {
    $r = Invoke-HwGet '/api/ping'
    $o = [ordered]@{ t0Ms = $r.T0; t3Ms = $r.T3; rttMs = ($r.T3 - $r.T0); ok = $false; t = $null }
    if ($r.Ok) { try { $pj = ConvertFrom-JsonText $r.Body; $o.ok = ($pj.ContainsKey('pong') -and $pj['pong'] -eq $true); $o.t = Get-Num $pj 't' } catch { } }
    if (-not $o.ok) { $o['error'] = $r.Error }
    if ($o.ok) { $pingOk++ }
    $okText = 'false'; if ($o.ok) { $okText = 'true' }
    $pingLine = '{{"t0Ms":{0},"t3Ms":{1},"rttMs":{2},"ok":{3},"t":{4}}}' -f (Format-Num $o.t0Ms), (Format-Num $o.t3Ms), (Format-Num $o.rttMs), $okText, (Format-Num $o.t)
    [void]$pingLines.Append($pingLine + "`n")
    Start-Sleep -Milliseconds 100
}
Write-Text (Join-Path $dir 'pings.jsonl') $pingLines.ToString()
if ($pingOk -eq 0) { Write-Host '[중단] /api/ping 응답 없음.'; exit 2 }

$sdbResp = Invoke-HwGet '/api/sensors/sdb'
if (-not $sdbResp.Ok) { Write-Host "[중단] /api/sensors/sdb 실패: $($sdbResp.Status) $($sdbResp.Error)"; exit 2 }
$sdbDict = Get-JsonRoot $sdbResp.Body
if ((Get-Num $sdbDict 'Version') -ne 1) { Write-Host '[중단] SDB Version 이 1이 아닙니다.'; exit 2 }
Write-Text (Join-Path $dir 'sdb.json') $sdbResp.Body
if ($sdbDict.ContainsKey('_notice') -and ([string]$sdbDict['_notice']) -match 'SYNTHETIC') { $source = 'SYNTHETIC'; $sourceReason = '응답에 SYNTHETIC 표시(_notice)가 있음 — 모의 서버' }
$wvInSdb = Test-SdbHasWireView $sdbDict
if ($wvInSdb -and $wvClients.Count -gt 0) {
    Write-Host ("[중단] HWiNFO가 WireView를 읽는 중인데 다른 WireView 클라이언트가 실행 중입니다: {0}. 동시 접근 시험은 승인 전 금지이므로 수집하지 않습니다. 해당 앱을 직접 종료한 뒤 다시 실행하세요(이 스크립트는 프로세스를 종료하지 않음)." -f (@($wvClients | ForEach-Object { "$($_.name) PID $($_.pid)" }) -join ', '))
    exit 5
}

$baseRel = $null
if ($BaselineFile) {
    Copy-Item -LiteralPath $BaselineFile -Destination (Join-Path $dir 'baseline.json'); $baseRel = 'baseline.json'
}

# CPU는 포트 소유자가 HWiNFO로 확인되면 출처와 무관하게 잰다(위에서 HWiNFO가 아닌 소유자는 이미 중단)
$hwPid = $null; if ($null -ne $owner.pid) { $hwPid = [int]$owner.pid }
$manifest = [ordered]@{
    schema = 'iron.rc3.run/1'; source = $source; sourceReason = $sourceReason; collector = 'Collect-HWiNfoRest.ps1 (RC3)'; gate = $Gate
    url = $BaseUrl; startedAtUtc = [DateTime]::UtcNow.ToString('o'); samplesRequested = $Samples; intervalMs = $IntervalMs
    endpointOwner = [ordered]@{ pid = $owner.pid; name = $owner.name; path = $owner.path; version = (Get-FileVersionInfo $owner.path) }
    operatorAttestation = $(if ($Gate -eq 'G3A') { [ordered]@{ hwinfoIsOnlyWireViewClient = [bool]$OperatorAttestsHWiNFOExclusive; note = '운영자가 다른 WireView 클라이언트를 직접 종료했다고 확인' } } else { $null })
    evidence = [ordered]@{ wireViewInSdb = $wvInSdb; wireViewClientProcesses = $wvClients; wireViewClientChecks = 0; pnp = (Get-PnpEvidence); ports = [ordered]@{ basalt = (Get-Listener $basaltPort); relay = (Get-Listener $relayPort) } }
    baselineFile = $baseRel; logicalProcessors = [Environment]::ProcessorCount; powershell = $PSVersionTable.PSVersion.ToString()
}
Write-Host ("[Collect] {0} · {1}샘플 · {2} ms · 출처 {3} → {4}" -f $Gate, $Samples, $IntervalMs, $source, $dir)

$lp = [Environment]::ProcessorCount
$prevCpu = $null; $prevCpuT = $null
if ($hwPid) { $prevCpu = Get-CpuSeconds $hwPid; $prevCpuT = Get-NowMs }
$writer = New-Object IO.StreamWriter((Join-Path $dir 'samples_all.jsonl'), $false, $Utf8)
$aborted = $null
# RC3.2: 수집 루프의 요청 시간 제한 = 간격의 2배(1~5초). Windows는 닫힌 포트(HWiNFO 꺼짐)에 연결할 때 실패까지
# 약 2초를 쓰므로, 5초 제한이면 끊긴 동안 요청 하나가 여러 주기를 붙잡는다. 놓친 주기는 따라잡지 않는다.
$script:RequestTimeoutSec = [int][Math]::Min(5, [Math]::Max(1, [Math]::Ceiling(2.0 * $IntervalMs / 1000.0)))
$start = $script:Clock.ElapsedMilliseconds
$skippedLate = 0
try {
    for ($i = 1; $i -le $Samples; $i++) {
        $due = ($i - 1) * [long]$IntervalMs
        if (($script:Clock.ElapsedMilliseconds - $start) - $due -ge $IntervalMs) {
            # 이 샘플의 주기가 통째로 지났다: 요청하지 않고 빈칸으로 기록(밀린 샘플을 연달아 보내지 않음)
            $skippedLate++
            $writer.WriteLine(('{{"i":{0},"t0Ms":null,"t3Ms":null,"rttMs":null,"ok":false,"status":null,"error":{1},"cpuPct":null,"read":null}}' -f $i, (ConvertTo-Json -InputObject $script:SkippedLate -Compress)))
            if (($i % 10) -eq 0) { $writer.Flush(); Write-Host ("  {0}/{1}" -f $i, $Samples) }
            continue
        }
        $wait = $due - ($script:Clock.ElapsedMilliseconds - $start)
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
        $r = Invoke-HwGet '/api/sensors/read'
        $cpuPct = $null
        if ($hwPid -and $null -ne $prevCpu) {
            $c = Get-CpuSeconds $hwPid; $ct = Get-NowMs
            if ($null -ne $c -and $ct -gt $prevCpuT) { $cpuPct = ($c - $prevCpu) * 1000.0 / ($ct - $prevCpuT) / $lp * 100.0; $prevCpu = $c; $prevCpuT = $ct }
        }
        $readJson = 'null'; $ok = $false; $err = $r.Error
        if ($r.Ok) {
            try {
                $rd = Get-JsonRoot $r.Body
                $ok = $true
                # JSON 문자열 안의 줄바꿈은 이스케이프되어 있으므로, 원문 줄바꿈은 공백으로 바꿔도 같은 JSON이다
                $readJson = $r.Body.Replace("`r", ' ').Replace("`n", ' ')
                if ($i -eq 1 -and $rd.ContainsKey('_notice') -and ([string]$rd['_notice']) -match 'SYNTHETIC') { $manifest.source = 'SYNTHETIC'; $manifest.sourceReason = '응답에 SYNTHETIC 표시(_notice)가 있음 — 모의 서버' }
            }
            catch { $err = 'JSON 해석 실패: ' + $_.Exception.Message }
        }
        $errJson = 'null'; if ($err) { $errJson = (ConvertTo-Json -InputObject ([string]$err) -Compress) }
        $statusJson = 'null'; if ($null -ne $r.Status) { $statusJson = [string]$r.Status }
        $writer.WriteLine(('{{"i":{0},"t0Ms":{1},"t3Ms":{2},"rttMs":{3},"ok":{4},"status":{5},"error":{6},"cpuPct":{7},"read":{8}}}' -f $i, (Format-Num $r.T0), (Format-Num $r.T3), (Format-Num ($r.T3 - $r.T0)), $(if ($ok) { 'true' } else { 'false' }), $statusJson, $errJson, (Format-Num $cpuPct), $readJson))
        if (($i % 10) -eq 0) {
            $writer.Flush()
            $manifest.evidence.wireViewClientChecks++
            if ($wvInSdb) {
                $now = Get-ProcessesByNames $Ev['wireViewClientProcessNames']
                if ($now.Count -gt 0) { $manifest.evidence.wireViewClientProcesses = $now; $aborted = '수집 중 다른 WireView 클라이언트 실행 감지'; break }
            }
            Write-Host ("  {0}/{1}" -f $i, $Samples)
        }
    }
}
finally { $writer.Dispose() }

$manifest['finishedAtUtc'] = [DateTime]::UtcNow.ToString('o')
$manifest['skippedLate'] = $skippedLate
$manifest['requestTimeoutSec'] = $script:RequestTimeoutSec
if ($aborted) { $manifest['aborted'] = $aborted }
Write-Text (Join-Path $dir 'run_manifest.json') (ConvertTo-JsonText $manifest)
if ($aborted) { Write-Host "[중단] $aborted — 동시 접근 시험은 승인 전 금지. 수집을 멈췄습니다(프로세스는 건드리지 않음)."; exit 5 }
$g1 = Measure-G1 $dir
Write-Text (Join-Path $dir 'g1_report.json') (ConvertTo-JsonText $g1)
Show-G1 $g1 $dir
exit (Get-ExitCode $g1)
