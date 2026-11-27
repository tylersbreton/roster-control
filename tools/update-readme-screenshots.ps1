# Refreshes the README screenshots (docs/images) from a UI test run. Part of every release: see README → Releasing.
# Usage: .\tools\update-readme-screenshots.ps1 [-SkipTests]
#   Runs the headless UI tests (they capture every screen to artifacts\screenshots), then copies the shots listed in
#   tools\readme-screenshots.txt to docs\images and removes images there that are no longer listed.
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipTests) {
    dotnet test tests/RosterControl.UITests
    if ($LASTEXITCODE -ne 0) { throw "UI tests failed; screenshots not updated" }
}

$names = Get-Content tools/readme-screenshots.txt | Where-Object { $_ -and -not $_.StartsWith("#") } | ForEach-Object { $_.Trim() }
$source = Join-Path $root "artifacts/screenshots"
$target = Join-Path $root "docs/images"
New-Item -ItemType Directory -Force $target | Out-Null

foreach ($name in $names) {
    $file = Join-Path $source "$name.png"
    if (-not (Test-Path $file)) { throw "Missing screenshot $name.png (run without -SkipTests)" }
    Copy-Item $file (Join-Path $target "$name.png") -Force
}
Get-ChildItem $target -Filter *.png | Where-Object { $names -notcontains $_.BaseName } | ForEach-Object {
    Remove-Item $_.FullName
    Write-Host "Removed $($_.Name) (no longer in the README)"
}
Write-Host "Updated $($names.Count) screenshots in docs\images. Check README.md shows the new features too."
