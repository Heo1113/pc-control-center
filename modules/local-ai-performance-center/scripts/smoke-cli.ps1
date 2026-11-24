<#
.SYNOPSIS
  CLI smoke test on Windows: real Windows CPU/RAM sensors (NVML if an NVIDIA driver is present),
  a local mock Strata server, two recordings, comparison, export, backup and integrity check.
#>
param([string]$Configuration = "Release", [int]$Port = 18080)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$data = Join-Path $temp "lapc-cli-smoke-$(Get-Random)"
$mockDll = Join-Path $root "tests/LocalAiPerf.MockStrata/bin/$Configuration/net10.0/LocalAiPerf.MockStrata.dll"
$cliDll = Join-Path $root "src/LocalAiPerf.Cli/bin/$Configuration/net10.0/lapc.dll"
$url = "http://127.0.0.1:$Port"

function Invoke-Cli([string[]]$CliArgs) {
    Write-Host "> lapc $($CliArgs -join ' ')"
    & dotnet $cliDll @CliArgs
    if ($LASTEXITCODE -ne 0) { throw "lapc $($CliArgs[0]) failed with exit code $LASTEXITCODE" }
}

$mock = Start-Process -FilePath dotnet -ArgumentList @($mockDll, '--port', $Port) -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $temp 'mock-cli.out') -RedirectStandardError (Join-Path $temp 'mock-cli.err')
try {
    Start-Sleep -Seconds 3
    Invoke-Cli @('sensors', '--data-dir', $data)
    Invoke-Cli @('strata', '--strata-url', $url, '--data-dir', $data)
    Invoke-Cli @('record', '--seconds', '8', '--interval-ms', '500', '--name', 'CI A', '--strata-url', $url, '--data-dir', $data)
    Invoke-Cli @('record', '--seconds', '6', '--interval-ms', '500', '--name', 'CI B', '--strata-url', $url, '--data-dir', $data)
    Invoke-Cli @('sessions', '--data-dir', $data)
    Invoke-Cli @('compare', '--sessions', '1,2', '--out', (Join-Path $data 'compare.json'), '--data-dir', $data)
    Invoke-Cli @('export', '--session', '1', '--format', 'json', '--out', (Join-Path $data 'session1.json'), '--data-dir', $data)
    Invoke-Cli @('export', '--session', '1', '--format', 'csv', '--out', (Join-Path $data 'session1-csv'), '--data-dir', $data)
    Invoke-Cli @('backup', '--out', (Join-Path $data 'backup.db'), '--data-dir', $data)
    Invoke-Cli @('check', '--data-dir', $data)

    $json = Get-Content (Join-Path $data 'session1.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $system = @($json.samples.system).Count
    $runtime = @($json.samples.runtime | Where-Object { $_.state -eq 'Connected' }).Count
    $cpu = @($json.samples.system | Where-Object { $null -ne $_.cpuUtil }).Count
    $ram = @($json.samples.system | Where-Object { $null -ne $_.ramUsed }).Count
    if ($json.session.isMock) { throw 'The CLI smoke test must use the real Windows sensors.' }
    if ($system -lt 10) { throw "Too few system samples: $system" }
    if ($cpu -lt 5 -or $ram -lt 5) { throw "Windows CPU/RAM readings missing (cpu=$cpu, ram=$ram)" }
    if ($runtime -lt 5) { throw "Too few connected runtime samples: $runtime" }
    Write-Host "CLI smoke test OK: system=$system cpu=$cpu ram=$ram runtime(connected)=$runtime gpu=$(@($json.samples.gpu).Count)"
}
finally {
    if ($mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}
