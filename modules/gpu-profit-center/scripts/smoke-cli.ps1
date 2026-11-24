<#
.SYNOPSIS
  Command line smoke test: MOCK demo data, sessions, CSV/JSON export, periods, backup (and refusal to overwrite),
  backup verification, recalculation, status. Runs the built gpc (dotnet run) or a published gpc.exe.
#>
param(
    [string]$Configuration = "Release",
    [string]$ReportDir = "dist/smoke/cli",
    [string]$Exe = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path $ReportDir).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$data = Join-Path $temp "gpc-cli-smoke-$(Get-Random)"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $Exe) {
    $Exe = Join-Path $root "src/GpuProfit.Cli/bin/$Configuration/net10.0/gpc.exe"
}

function Gpc([string[]]$CliArgs, [int]$Expect = 0) {
    $out = & $Exe @CliArgs --data-dir $data 2>&1
    $code = $LASTEXITCODE
    $text = ($out | Out-String)
    Write-Host "gpc $($CliArgs -join ' ') -> $code"
    Write-Host $text
    if ($code -ne $Expect) { throw "gpc $($CliArgs -join ' ') exited with $code (expected $Expect)" }
    return $text
}

Gpc @('demo-data') | Out-Null
Gpc @('sessions') | Out-Null
Gpc @('export', '--out', (Join-Path $ReportDir 'sessions.csv')) | Out-Null
Gpc @('export', '--format', 'json', '--out', (Join-Path $ReportDir 'ledger.json')) | Out-Null
Gpc @('periods', '--by', 'week', '--out', (Join-Path $ReportDir 'periods-week.csv')) | Out-Null
$backup = Join-Path $ReportDir 'backup.db'
Gpc @('backup', '--out', $backup) | Out-Null
Gpc @('backup', '--out', $backup) 1 | Out-Null
Gpc @('verify-backup', $backup) | Out-Null
Gpc @('recalc') | Out-Null
$status = Gpc @('status', '--json')
Set-Content -Path (Join-Path $ReportDir 'status.json') -Value $status -Encoding utf8
$parsed = $status | ConvertFrom-Json
if ($parsed.integrity -ne 'ok') { throw "integrity: $($parsed.integrity)" }
if ($parsed.database.Sessions -lt 6) { throw "expected the 6 demo sessions" }
$ledger = Get-Content (Join-Path $ReportDir 'ledger.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($ledger.format -ne 'gpu-profit-center/ledger') { throw 'ledger JSON format' }
Write-Host 'CLI smoke test OK.'
