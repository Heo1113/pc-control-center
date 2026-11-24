<#
.SYNOPSIS
  Builds the portable Windows x64 package (self-contained single-file EXE, no .NET install needed).
.EXAMPLE
  ./scripts/publish.ps1 -OutputDir dist
#>
param(
    [string]$OutputDir = "dist",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = 'LocalAiPerformanceCenter-win-x64'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$appOut = Join-Path $OutputDir $name
if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }

dotnet publish (Join-Path $root 'src/LocalAiPerf.App/LocalAiPerf.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Only the EXE and the documents go into the package.
Get-ChildItem $appOut -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root 'README.md') $appOut
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $appOut
$marker = "이 파일이 있으면 데이터를 실행 파일 옆 data 폴더에 저장합니다 (포터블 모드).`r`n" +
          "This file enables portable mode: data is stored in the 'data' folder next to the EXE.`r`n" +
          "Delete it to use %LOCALAPPDATA%\LocalAiPerformanceCenter instead."
Set-Content -Path (Join-Path $appOut 'portable.txt') -Value $marker -Encoding utf8

$exe = Join-Path $appOut 'LocalAiPerformanceCenter.exe'
if (-not (Test-Path $exe)) { throw "EXE was not produced: $exe" }
"$((Get-FileHash $exe -Algorithm SHA256).Hash.ToLower())  LocalAiPerformanceCenter.exe" | Set-Content "$exe.sha256" -Encoding ascii

$zip = Join-Path $OutputDir "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zip
"$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())  $name.zip" | Set-Content "$zip.sha256" -Encoding ascii

Get-ChildItem $appOut | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
