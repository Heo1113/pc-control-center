<#
.SYNOPSIS
  Builds the portable Windows x64 package of PC Control Center: PcControlCenter.exe (the one integrated window),
  PcControlCenter.OperatorHost.exe (the Operator's separate desktop automation process, started by the window only) and
  OperatorTestTarget.exe (E's safe test app), self-contained single-file EXEs (no .NET install needed). Same approach as the original programs' publish.ps1.
  The original programs' own EXEs and command-line tools are not part of the package.
.EXAMPLE
  ./scripts/publish.ps1 -OutputDir dist
#>
param(
    [string]$OutputDir = "dist",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = 'PcControlCenter-win-x64'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$appOut = Join-Path $OutputDir $name
if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }

$common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-o', $appOut)
# PcControlCenter.exe (the window), PcControlCenter.OperatorHost.exe (E's desktop automation, separate process) and
# OperatorTestTarget.exe (project E's safe test app: it only changes its own window, used by E's built-in workflow).
$projects = @(
    'src/PcControlCenter.App/PcControlCenter.App.csproj',
    'src/PcControlCenter.OperatorHost/PcControlCenter.OperatorHost.csproj',
    'modules/windows-ai-operator/src/WindowsAiOperator.TestTarget/WindowsAiOperator.TestTarget.csproj'
)
foreach ($project in $projects) {
    dotnet publish (Join-Path $root $project) @common
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project ($LASTEXITCODE)" }
}

Get-ChildItem $appOut -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root 'README.md') $appOut
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $appOut -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $appOut 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs/*.md') (Join-Path $appOut 'docs') -ErrorAction SilentlyContinue
$marker = "이 파일이 있으면 데이터를 실행 파일 옆 data 폴더에 저장합니다 (포터블 모드).`r`n" +
          "This file enables portable mode: data is stored in the 'data' folder next to the EXE.`r`n" +
          "Delete it to use %LOCALAPPDATA%\PcControlCenter instead."
Set-Content -Path (Join-Path $appOut 'portable.txt') -Value $marker -Encoding utf8

foreach ($exe in Get-ChildItem $appOut -Filter *.exe) {
    "$((Get-FileHash $exe.FullName -Algorithm SHA256).Hash.ToLower())  $($exe.Name)" | Set-Content "$($exe.FullName).sha256" -Encoding ascii
}
foreach ($required in @('PcControlCenter.exe', 'PcControlCenter.OperatorHost.exe', 'OperatorTestTarget.exe')) {
    if (-not (Test-Path (Join-Path $appOut $required))) { throw "$required was not produced" }
}

$zip = Join-Path $OutputDir "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zip
"$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())  $name.zip" | Set-Content "$zip.sha256" -Encoding ascii

Get-ChildItem $appOut | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
