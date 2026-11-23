<#
  basalt-probe.ps1 - Basalt 통합 0단계 확인 (읽기 전용)

  하는 일
    1) 127.0.0.1:<포트>가 비어 있는지 확인합니다. 이미 쓰이고 있으면 Basalt를 켜지 않고 끝냅니다.
    2) start-basalt.ps1의 매개변수 이름과 호출하는 명령 이름, 설치 폴더의 실행 파일 이름, 두 설정 파일의 키 구조를 읽습니다(파일은 고치지 않음).
    3) 새 창에서 품질용 설정으로 Basalt를 켜고(start-basalt.ps1 -Config basalt_config.json), /health가 응답하고 모델이 올라올 때까지 기다립니다.
    4) 주소마다 응답의 구조를 기록합니다: 키 이름, 숫자·참거짓 값, 문자열은 길이만.
       문자열 값은 service, status, version, engine, model 같은 짧은 식별 값만 80자까지 남기고, 경로처럼 보이면 파일 이름만 남깁니다.
       이름에 key, token, secret, password, auth, cookie, session, credential이 들어간 값은 "있음/비어 있음"만 남깁니다.
    5) 대시보드(/)와 그 스크립트가 부르는 API 경로를 찾고, 동작을 일으킬 수 있는 이름(load, chat, shutdown, save 등)을 뺀 GET만 불러 봅니다.
    6) 포트를 가진 프로세스와 실행 창 아래 프로세스 트리를 기록합니다: 이름, PID, 부모 PID, 시작 시각(명령줄은 읽지 않음).
    7) 정상 종료: 기본으로 Basalt 창의 콘솔에 Ctrl+C 신호를 보냅니다(창에서 Ctrl+C를 누를 때와 같은 신호, 앱이 쓸 방법의 시험).
       -ManualStop을 주면 사용자께서 창에서 직접 Ctrl+C를 누르시면 됩니다. 신호로 끝나지 않으면 직접 끄시도록 안내합니다(강제 종료는 하지 않음).
    8) 남은 프로세스(같은 PID이면서 같은 시작 시각)와 포트 상태를 확인합니다.

  하지 않는 일
    C:\AI\basalt 안의 파일을 고치지 않습니다.
    -KeyVariable을 주지 않으면 API 키를 보내지 않습니다(401이면 그렇게 기록). 주면 그 환경 변수의 값을 127.0.0.1에만 보내고 화면이나 파일에 남기지 않습니다.
    프로세스를 강제로 끝내지 않고 시스템 설정을 바꾸지 않습니다(실행 정책 Bypass는 이 스크립트가 띄우는 PowerShell 프로세스에만 적용).

  결과: %TEMP%\basalt-probe.json (값 대신 구조가 들어 있습니다. 열어 보신 뒤 대화에 첨부해 주세요.)
  실행: powershell -NoProfile -ExecutionPolicy Bypass -File .\basalt-probe.ps1
  키를 쓰는 경우: powershell -NoProfile -ExecutionPolicy Bypass -File .\basalt-probe.ps1 -KeyVariable STRATA_TELEMETRY_KEY
#>
param(
    [string]$BasaltDir = 'C:\AI\basalt',
    [string]$ConfigName = 'basalt_config.json',
    [string]$FastConfigName = 'basalt_config.fast.json',
    [int]$Port = 8080,
    [int]$StartTimeoutSec = 900,
    [int]$StopTimeoutSec = 60,
    [string]$KeyVariable = '',
    [switch]$ManualStop,
    [string]$OutFile = (Join-Path $env:TEMP 'basalt-probe.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$base = "http://127.0.0.1:$Port"
$keepStrings = @('service', 'status', 'state', 'version', 'engine', 'engine_kind', 'engine_version', 'kv', 'kv_type', 'cache_type',
    'object', 'model', 'id', 'owned_by', 'mode', 'profile', 'preset', 'host', 'quant', 'type', 'source', 'unit', 'units', 'backend', 'device')
$secretName = '(?i)(key|token|secret|passw|auth|cookie|session|credential)'
$report = [ordered]@{
    probe = 'basalt-probe 2'; probedAt = (Get-Date).ToString('o'); port = $Port; keySent = $false
    powershell = $PSVersionTable.PSVersion.ToString(); windows = [Environment]::OSVersion.Version.ToString()
}

function Save-Report { $report | ConvertTo-Json -Depth 16 | Set-Content -Encoding UTF8 -LiteralPath $OutFile }

function Get-Shape($value, [int]$depth, [string]$name, [bool]$secret) {
    if ($name -and $name -match $secretName) { $secret = $true }
    if ($null -eq $value) { return 'null' }
    if ($value -is [bool]) { return "bool: $value" }
    if ($secret -and $value -is [string]) { if ($value.Length -gt 0) { return '비밀 값: 있음' } else { return '비밀 값: 비어 있음' } }
    if ($depth -gt 6) { return '...' }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $o = [ordered]@{}
        foreach ($prop in $value.PSObject.Properties) { $o[$prop.Name] = Get-Shape $prop.Value ($depth + 1) $prop.Name $secret }
        return $o
    }
    if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
        $items = @($value)
        if ($items.Count -eq 0) { return 'array(0)' }
        return [ordered]@{ count = $items.Count; first = (Get-Shape $items[0] ($depth + 1) $name $secret) }
    }
    if ($value -is [string]) {
        if ($keepStrings -contains $name) {
            if ($value -match '[\\/]' -or $value -match '^[A-Za-z]:') {
                $leaf = ($value -split '[\\/]')[-1]
                if ($leaf.Length -gt 80) { $leaf = $leaf.Substring(0, 80) }
                return "경로(파일 이름만): $leaf"
            }
            if ($value.Length -gt 80) { return "value: $($value.Substring(0, 80))" }
            return "value: $value"
        }
        return "string($($value.Length))"
    }
    if ($value -is [datetime]) { return 'datetime' }
    if ($secret) { return 'number' }
    if ($value -is [ValueType]) { return "number: $value" }
    return $value.GetType().Name
}

$credential = $null
if ($KeyVariable) {
    $credential = [Environment]::GetEnvironmentVariable($KeyVariable, 'Process')
    if (-not $credential) { $credential = [Environment]::GetEnvironmentVariable($KeyVariable, 'User') }
    if ($credential) { $report.keySent = $true; $report.keyVariable = $KeyVariable }
    else { Write-Host "환경 변수 $KeyVariable 에 값이 없어 키 없이 확인합니다." }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(15)

function Invoke-Get([string]$path, [bool]$withKey) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, ($base + $path))
    if ($withKey -and $credential) {
        $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $credential)
    }
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $scheme = $null
        foreach ($h in $response.Headers.WwwAuthenticate) { $scheme = $h.Scheme; break }
        $server = (@($response.Headers.Server) | ForEach-Object { $_.ToString() }) -join ' '
        $location = $null
        if ($response.Headers.Location) { $location = $response.Headers.Location.ToString() }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Type = [string]$response.Content.Headers.ContentType; Body = $body
            AuthScheme = $scheme; Server = $server; Location = $location; Error = $null }
    } catch {
        return [pscustomobject]@{ Status = 0; Type = ''; Body = ''; AuthScheme = $null; Server = ''; Location = $null
            Error = $_.Exception.GetBaseException().Message }
    } finally {
        $request.Dispose()
    }
}

function Get-Entry([string]$path, [bool]$withKey) {
    $r = Invoke-Get $path $withKey
    $entry = [ordered]@{ status = $r.Status; contentType = $r.Type; bytes = $r.Body.Length }
    if ($withKey -and $credential) { $entry.keySent = $true }
    if ($r.Error) { $entry.error = $r.Error }
    if ($r.AuthScheme) { $entry.authScheme = $r.AuthScheme }
    if ($r.Server) { $entry.server = $r.Server }
    if ($r.Location) { $entry.location = $r.Location }
    if ($r.Type -like '*json*') {
        try { $entry.shape = Get-Shape ($r.Body | ConvertFrom-Json) 0 '' $false } catch { $entry.shape = 'JSON으로 읽지 못함' }
    } elseif ($r.Status -eq 200 -and $r.Type -like 'text/plain*') {
        $entry.metricNames = @(($r.Body -split "`n") | ForEach-Object { if ($_ -match '^[A-Za-z_:][A-Za-z0-9_:]*') { $Matches[0] } } |
            Select-Object -Unique -First 60)
    }
    return $entry
}

function Get-Listeners([int]$p) {
    @(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)
}

function Get-ListenerInfo($list) {
    @($list | ForEach-Object {
        $proc = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        $procName = $null
        if ($proc) { $procName = $proc.ProcessName }
        [ordered]@{ pid = [int]$_.OwningProcess; name = $procName; address = [string]$_.LocalAddress }
    })
}

function Get-ScriptOutline([string]$path) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    $params = @()
    if ($ast.ParamBlock) {
        foreach ($p in $ast.ParamBlock.Parameters) { $params += [ordered]@{ name = $p.Name.VariablePath.UserPath; type = $p.StaticType.Name } }
    }
    $commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
    $names = @($commands | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | ForEach-Object { ($_ -split '[\\/]')[-1] } | Sort-Object -Unique)
    $startProcess = @($commands | Where-Object { $_.GetCommandName() -eq 'Start-Process' } | ForEach-Object {
        (@($_.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object { $_.ParameterName }) -join ' ')
    })
    return [ordered]@{ parseErrors = @($errors).Count; parameters = $params; commands = $names; startProcessParameters = $startProcess }
}

function Get-ConfigShape([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '없음' }
    try { return Get-Shape ((Get-Content -LiteralPath $path -Raw -Encoding UTF8) | ConvertFrom-Json) 0 '' $false }
    catch { return 'JSON으로 읽지 못함' }
}

function Get-Subtree([int]$rootId, $rootCreated, $all) {
    $result = @()
    $seen = @{ $rootId = $true }
    $queue = New-Object System.Collections.Queue
    $queue.Enqueue([pscustomobject]@{ Id = $rootId; Created = $rootCreated; Depth = 0 })
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        foreach ($c in $all) {
            $cid = [int]$c.ProcessId
            # A child must have started after its parent: a reused parent PID cannot adopt an older process.
            if ([int]$c.ParentProcessId -eq $item.Id -and -not $seen.ContainsKey($cid) -and $c.CreationDate -ge $item.Created) {
                $seen[$cid] = $true
                $result += [pscustomobject]@{ Proc = $c; Depth = $item.Depth + 1 }
                $queue.Enqueue([pscustomobject]@{ Id = $cid; Created = $c.CreationDate; Depth = $item.Depth + 1 })
            }
        }
    }
    return $result
}

function New-TreeEntry($proc, [int]$depth, [string]$role) {
    [ordered]@{ pid = [int]$proc.ProcessId; parent = [int]$proc.ParentProcessId; name = $proc.Name
        started = $proc.CreationDate.ToString('o'); depth = $depth; role = $role }
}

function Get-Alive($tree) {
    $now = @(Get-CimInstance Win32_Process -Property ProcessId, Name, CreationDate)
    foreach ($t in $tree) {
        foreach ($p in $now) {
            if ([int]$p.ProcessId -eq $t.pid -and $p.CreationDate.ToString('o') -eq $t.started) {
                [ordered]@{ pid = $t.pid; name = $t.name; role = $t.role }
                break
            }
        }
    }
}

function Send-CtrlC([int]$targetId) {
    # A hidden helper PowerShell leaves its own console, joins the target's console, ignores Ctrl+C itself and sends it there.
    # Exit codes: 0 sent, 2 could not join the console (the process has none), 3 could not send.
    $helper = @'
$sig = @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleSignal {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
"@
Add-Type -TypeDefinition $sig
[void][ConsoleSignal]::FreeConsole()
if (-not [ConsoleSignal]::AttachConsole(TARGET_PID)) { exit 2 }
[void][ConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
if (-not [ConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { exit 3 }
Start-Sleep -Seconds 2
exit 0
'@
    $helper = $helper.Replace('TARGET_PID', [string]$targetId)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helper))
    $p = Start-Process -FilePath 'powershell.exe' -PassThru -Wait -WindowStyle Hidden `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
    return $p.ExitCode
}

function Wait-Stopped($tree, [datetime]$since, [int]$seconds, $stop) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not $stop.Contains('secondsUntilPortFree') -and (Get-Listeners $Port).Count -eq 0) {
            $stop.secondsUntilPortFree = [math]::Round(((Get-Date) - $since).TotalSeconds, 1)
        }
        if (-not $stop.Contains('secondsUntilProcessesGone') -and @(Get-Alive $tree).Count -eq 0) {
            $stop.secondsUntilProcessesGone = [math]::Round(((Get-Date) - $since).TotalSeconds, 1)
        }
        if ($stop.Contains('secondsUntilPortFree') -and $stop.Contains('secondsUntilProcessesGone')) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$basaltStarted = $false
try {
    # 1) The port must be free.
    $report.basaltProcessesBefore = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'basalt%'" |
        ForEach-Object { [ordered]@{ pid = [int]$_.ProcessId; name = $_.Name } })
    $busy = Get-Listeners $Port
    if ($busy.Count -gt 0) {
        $report.portBusyBeforeStart = Get-ListenerInfo $busy
        Save-Report
        Write-Host "포트 $Port 를 이미 다른 프로세스가 쓰고 있어 Basalt를 켜지 않았습니다. 결과: $OutFile"
        return
    }

    # 2) Read-only look at the installation.
    $script = Join-Path $BasaltDir 'start-basalt.ps1'
    $config = Join-Path $BasaltDir $ConfigName
    $fastConfig = Join-Path $BasaltDir $FastConfigName
    if (-not (Test-Path -LiteralPath $script) -or -not (Test-Path -LiteralPath $config)) {
        throw "start-basalt.ps1 또는 $ConfigName 을(를) $BasaltDir 에서 찾지 못했습니다."
    }
    $report.startScript = Get-ScriptOutline $script
    $report.configs = [ordered]@{ quality = Get-ConfigShape $config; fast = Get-ConfigShape $fastConfig }
    $root = (Resolve-Path -LiteralPath $BasaltDir).Path.TrimEnd('\')
    $report.executables = @(Get-ChildItem -LiteralPath $BasaltDir -Recurse -Depth 3 -Filter *.exe -File -ErrorAction SilentlyContinue |
        Select-Object -First 60 | ForEach-Object { $_.FullName.Substring($root.Length + 1) })

    # 3) Start Basalt (quality) in a window of its own and wait until it answers and has a model.
    $started = Get-Date
    $launcher = Start-Process -FilePath 'powershell.exe' -WorkingDirectory $BasaltDir -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"", '-Config', "`"$config`"")
    $basaltStarted = $true
    $null = $launcher.Handle # keeps ExitCode and ExitTime readable in Windows PowerShell 5.1
    $launcherInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($launcher.Id)"
    if (-not $launcherInfo) { throw 'Basalt 실행 창이 바로 닫혔습니다. start-basalt.ps1을 직접 실행해 오류를 확인해 주세요.' }
    Write-Host "Basalt를 새 창에서 켰습니다. /health 응답과 모델 로드를 기다립니다 (최대 $StartTimeoutSec 초)..."

    $healthy = $false; $firstOk = $null
    while (((Get-Date) - $started).TotalSeconds -lt $StartTimeoutSec) {
        if ($launcher.HasExited -and (Get-Listeners $Port).Count -eq 0 -and
            @(Get-CimInstance Win32_Process -Filter "Name LIKE 'basalt%'").Count -eq 0 -and
            ((Get-Date) - $launcher.ExitTime).TotalSeconds -gt 30) {
            $report.launcherExitedEarly = [ordered]@{ exitCode = $launcher.ExitCode; afterSeconds = [math]::Round(($launcher.ExitTime - $started).TotalSeconds) }
            break
        }
        $h = Invoke-Get '/health' $false
        if ($h.Status -eq 200) {
            if (-not $healthy) {
                $healthy = $true; $firstOk = Get-Date
                $report.secondsUntilHealthy = [math]::Round(($firstOk - $started).TotalSeconds)
                try { $report.healthWhenFirstOk = Get-Shape ($h.Body | ConvertFrom-Json) 0 '' $false } catch { }
            }
            $loaded = $null
            try { $j = $h.Body | ConvertFrom-Json; if ($j.PSObject.Properties.Name -contains 'loaded') { $loaded = [bool]$j.loaded } } catch { }
            if ($loaded -ne $false) { $report.secondsUntilReady = [math]::Round(((Get-Date) - $started).TotalSeconds); break }
            if (((Get-Date) - $firstOk).TotalSeconds -gt 300) { $report.stillNotLoadedAfter300s = $true; break }
        }
        Start-Sleep -Seconds 3
    }
    $report.healthy = $healthy

    # 4) The local AI module's six addresses (it sends the key to all but /health), then other Strata routes.
    $report.endpoints = [ordered]@{}
    foreach ($path in @('/health', '/v1/status', '/v1/telemetry', '/metrics', '/props', '/v1/models',
            '/api/health', '/status', '/models', '/slots', '/settings', '/config', '/api/requests', '/metrics/prometheus')) {
        $report.endpoints[$path] = Get-Entry $path ($path -ne '/health' -and $path -ne '/api/health')
    }

    # 5) The dashboard page, its scripts, and the API paths they use.
    $pagePath = '/'
    $page = Invoke-Get $pagePath $false
    if ($page.Status -ge 300 -and $page.Status -lt 400 -and $page.Location) {
        $pagePath = (New-Object Uri((New-Object Uri("$base/")), $page.Location)).PathAndQuery
        $page = Invoke-Get $pagePath $false
    }
    $report.dashboard = [ordered]@{ path = $pagePath; status = $page.Status; contentType = $page.Type; scripts = @(); apiPaths = @(); calledExtra = [ordered]@{} }
    if ($page.Status -eq 200) {
        $code = $page.Body
        $srcs = @([regex]::Matches($page.Body, '<script[^>]+src=["'']([^"'']+)["'']') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        $report.dashboard.scripts = $srcs
        foreach ($src in $srcs) {
            $resolved = New-Object Uri((New-Object Uri("$base$pagePath")), $src)
            if ($resolved.Host -eq '127.0.0.1' -and $resolved.Port -eq $Port) {
                $s = Invoke-Get $resolved.PathAndQuery $false
                if ($s.Status -eq 200) { $code += "`n" + $s.Body }
            }
        }
        $prefixes = 'v1|api|metrics|health|status|props|slots|settings|config|models|load|unload|mcp|tool-media|images|shutdown|stop|restart'
        $pattern = '["''`}](/(?:' + $prefixes + ')[A-Za-z0-9_\-\./]*)'
        $api = @([regex]::Matches($code, $pattern) | ForEach-Object { $_.Groups[1].Value.TrimEnd('/') } | Where-Object { $_ } | Sort-Object -Unique)
        $report.dashboard.apiPaths = $api
        $deny = '(?i)(load|chat|complet|messages|responses|generat|count_tokens|vram|mcp|shutdown|stop|restart|kill|exit|reset|clear|delete|erase|save|update|cancel|upload|tool-media|images|slots/)'
        $static = '(?i)\.(js|mjs|css|png|svg|jpe?g|gif|ico|woff2?|ttf|map|html?)$'
        foreach ($path in @($api | Where-Object { $_ -notmatch $deny -and $_ -notmatch $static -and -not $report.endpoints.Contains($_) } | Select-Object -First 25)) {
            $report.dashboard.calledExtra[$path] = Get-Entry $path $true
        }
    }

    # 6) Who owns the port, and the process tree under the window this script opened.
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CreationDate)
    $listeners = Get-Listeners $Port
    $owners = @($listeners | ForEach-Object { [int]$_.OwningProcess } | Sort-Object -Unique)
    $report.portOwners = Get-ListenerInfo $listeners
    $tree = @()
    $tree += New-TreeEntry $launcherInfo 0 '실행 창'
    foreach ($item in (Get-Subtree $launcher.Id $launcherInfo.CreationDate $all)) {
        $role = '하위'
        if ($owners -contains [int]$item.Proc.ProcessId) { $role = '포트 소유' }
        $tree += New-TreeEntry $item.Proc $item.Depth $role
    }
    foreach ($p in $all) {
        $id = [int]$p.ProcessId
        if (@($tree | Where-Object { $_.pid -eq $id }).Count -gt 0) { continue }
        if (($owners -contains $id) -or $p.Name -like 'basalt*') {
            $role = '트리 밖'
            if ($owners -contains $id) { $role = '포트 소유 (트리 밖)' }
            $tree += New-TreeEntry $p -1 $role
            foreach ($item in (Get-Subtree $id $p.CreationDate $all)) {
                if (@($tree | Where-Object { $_.pid -eq [int]$item.Proc.ProcessId }).Count -eq 0) { $tree += New-TreeEntry $item.Proc $item.Depth '트리 밖 하위' }
            }
        }
    }
    $report.processTree = $tree
    Save-Report

    # 7) Graceful stop: Ctrl+C to Basalt's console (or by hand), then wait without killing anything.
    $stop = [ordered]@{ method = 'signal' }
    if ($ManualStop) { $stop.method = 'manual' }
    $stopStart = Get-Date
    $done = $false
    if (-not $ManualStop) {
        $target = $launcher.Id
        $stop.signalTarget = '실행 창의 콘솔'
        if ($owners.Count -gt 0) { $target = $owners[0]; $stop.signalTarget = '포트 소유 프로세스의 콘솔' }
        Write-Host 'Basalt 콘솔에 Ctrl+C 신호를 보냅니다...'
        $exitCode = Send-CtrlC $target
        $stop.signalExitCode = $exitCode
        switch ($exitCode) { 0 { $stop.signal = '보냄' } 2 { $stop.signal = '콘솔에 붙지 못함 (콘솔이 없는 프로세스)' } 3 { $stop.signal = '보내지 못함' } default { $stop.signal = '도우미 오류' } }
        $done = Wait-Stopped $tree $stopStart $StopTimeoutSec $stop
        $stop.stoppedBySignal = $done
    }
    if (-not $done) {
        Write-Host ''
        Write-Host 'Basalt 창에서 Ctrl+C를 눌러 서버를 끄세요(창을 닫으셔도 됩니다). 이 창이 최대 10분 동안 자동으로 확인합니다.'
        $manualStart = Get-Date
        $done = Wait-Stopped $tree $stopStart 600 $stop
        $stop.manualWaitSeconds = [math]::Round(((Get-Date) - $manualStart).TotalSeconds)
    }
    $report.stop = $stop

    # 8) What is left?
    Start-Sleep -Seconds 3
    $after = Get-Listeners $Port
    $report.afterStop = [ordered]@{ processesLeft = @(Get-Alive $tree); portStillListening = ($after.Count -gt 0); portOwners = Get-ListenerInfo $after }
    $basaltStarted = $false
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    Save-Report
    Write-Host ''
    Write-Host "결과: $OutFile"
    if ($report.Contains('afterStop')) {
        Write-Host "남은 프로세스: $(@($report.afterStop.processesLeft).Count)개, 포트 $Port 사용 중: $($report.afterStop.portStillListening)"
    }
    if ($basaltStarted) { Write-Host 'Basalt가 아직 켜져 있을 수 있습니다. Basalt 창에서 Ctrl+C로 꺼 주세요.' }
}
