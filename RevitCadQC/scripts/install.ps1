<#
  Installs the add-in for the current Windows user (no admin rights needed).
  Usage:  .\scripts\install.ps1                  (every Revit version that has a build)
          .\scripts\install.ps1 -Versions 2025
          .\scripts\install.ps1 -Uninstall
#>
param([string[]]$Versions = @("2024", "2025", "2026"), [string]$Configuration = "Release", [switch]$Uninstall)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
foreach ($v in $Versions) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $target = Join-Path $addins "RevitCadQC"
    $manifest = Join-Path $addins "CadQC.addin"
    if ($Uninstall) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        if (Test-Path $manifest) { Remove-Item $manifest -Force }
        Write-Host "Removed from Revit $v"
        continue
    }
    $build = Join-Path $root "src\CadQC.Revit\bin\$Configuration\$v"
    if (-not (Test-Path (Join-Path $build "CadQC.Revit.dll"))) { Write-Host "No build for Revit $v (run scripts\build.ps1 -Versions $v) - skipped" -ForegroundColor Yellow; continue }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item "$build\*" $target -Recurse -Force -Exclude "CadQC.addin"
    Copy-Item (Join-Path $build "CadQC.addin") $manifest -Force
    Write-Host "Installed for Revit $v -> $target" -ForegroundColor Green
}
if (-not $Uninstall) {
    $oda = Get-ChildItem "$env:ProgramFiles\ODA" -Recurse -Filter ODAFileConverter.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    $acad = Get-ChildItem "$env:ProgramFiles\Autodesk" -Recurse -Depth 1 -Filter accoreconsole.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($oda) { Write-Host "DWG converter: $($oda.FullName)" }
    elseif ($acad) { Write-Host "DWG converter: $($acad.FullName)" }
    else { Write-Host "`nNo DWG converter found. Install the free ODA File Converter so DWG files are converted automatically:`n  https://www.opendesign.com/guestfiles/oda_file_converter" -ForegroundColor Yellow }
}
