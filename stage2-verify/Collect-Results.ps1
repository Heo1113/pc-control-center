<#
  results 폴더를 zip 하나로 묶습니다(전송은 하지 않음). 공유 전에 summary.md의 "민감정보 자체 점검"과
  probe.json의 commandLine 값을 직접 확인하세요. 명령줄은 IRON이 이미 가린 형태(--token=***)만 담습니다.
#>
param([string]$Results = (Join-Path $PSScriptRoot '..\results'))
$zip = Join-Path (Split-Path $Results) ('IRON_Stage2_results_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.zip')
Compress-Archive -Path (Join-Path $Results '*') -DestinationPath $zip
Write-Host "묶음: $zip"
