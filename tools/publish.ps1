# Reproducible release build: self-contained Windows x64 portable application (no .NET install required).
# Usage: .\tools\publish.ps1 [-Version 1.0.0] [-SkipTests]
# Output: artifacts\publish\RosterControl-<version>-win-x64\ (+ .zip and .sha256)
# The app stores its data in a "data" folder next to RosterControl.exe, so every unzipped copy has its own rosters.
param([string]$Version = "1.0.0", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipTests) {
    dotnet test tests/RosterControl.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$out = Join-Path $root "artifacts/publish/RosterControl-$Version-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Not single-file: Avalonia/SkiaSharp/SQLite native libraries load most reliably side-by-side.
dotnet publish src/RosterControl.Desktop/RosterControl.Desktop.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Never ship a developer's data folder.
$data = Join-Path $out "data"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }

$zip = Join-Path $root "artifacts/publish/RosterControl-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
