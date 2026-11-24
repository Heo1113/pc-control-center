<#
.SYNOPSIS
  Builds IRON Stage 1 as one self-contained Windows x64 file, Iron.App.exe (no .NET install needed), and a zip with
  its SHA-256. Stage 1 runs on mock data only; it touches no sensor, USB device, registry key or data file.
.EXAMPLE
  ./scripts/iron-publish.ps1 -OutputDir dist
#>
param(
    [string]$OutputDir = "dist",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = 'Iron.App-win-x64'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$out = Join-Path $OutputDir $name
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish (Join-Path $root 'src/Iron.App/Iron.App.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Get-ChildItem $out -Filter *.pdb | Remove-Item -Force
if (-not (Test-Path (Join-Path $out 'Iron.App.exe'))) { throw 'Iron.App.exe was not produced' }
Copy-Item (Join-Path $root 'docs/IRON_STAGE1.md') $out -ErrorAction SilentlyContinue
$exe = Join-Path $out 'Iron.App.exe'
"$((Get-FileHash $exe -Algorithm SHA256).Hash.ToLower())  Iron.App.exe" | Set-Content "$exe.sha256" -Encoding ascii

$zip = Join-Path $OutputDir "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
"$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())  $name.zip" | Set-Content "$zip.sha256" -Encoding ascii
Get-ChildItem $out | Format-Table Name, Length
