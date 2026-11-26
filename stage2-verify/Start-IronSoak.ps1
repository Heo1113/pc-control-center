<#
  IRON Stage 2 실기 검증 — 12시간 연속 실행 (실제 데이터 창)

  IRON 창을 실제 데이터 모드로 띄우고 1분마다 IRON 자신의 메모리·핸들·GDI·소스 상태를 CSV에 남깁니다.
  창은 평소처럼 쓰고(트레이로 내려도 됨) 12시간 뒤 Analyze-SoakLog.ps1로 분석합니다.
  주의: HWiNFO 무료판은 REST가 12시간 뒤 멈춘다는 2차 보도가 있습니다. 그 시점의 IRON 상태(OFFLINE 전환)도 기록됩니다.

    .\Start-IronSoak.ps1
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\results'))
$exe = Join-Path $PSScriptRoot '..\IRON\Iron.App.exe'
if (-not (Test-Path $exe)) { throw "Iron.App.exe 없음: $exe" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$csv = Join-Path $Out ('soak_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.csv')
Start-Process $exe -ArgumentList '--source', 'real', '--soak-log', $csv
Write-Host "IRON(실제 데이터) 시작. 기록: $csv"
Write-Host "12시간 뒤:  .\Analyze-SoakLog.ps1 -Csv `"$csv`""
