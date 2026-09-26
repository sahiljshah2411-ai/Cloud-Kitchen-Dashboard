<#
====================================================================
 REVIT CAD QC - UNINSTALL  (run RUN_3_UNINSTALL.bat)
====================================================================
 Removes the add-in from every Revit version for the current user.
 Keeps %APPDATA%\RevitCadQC (default settings) and every project's
 <model>_CadQC.json settings and QC reports.
====================================================================
#>
$ErrorActionPreference = "Continue"
trap {
    Write-Host ""
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "  Press Enter to close"
    exit 1
}
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Host "  Close Revit first - it locks the add-in DLL." -ForegroundColor Yellow
    Read-Host "  Press Enter to close"; exit 1
}
$removed = 0
foreach ($v in @("2022", "2023", "2024", "2025", "2026")) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $dir = Join-Path $addins "RevitCadQC"
    foreach ($m in "RevitCadQC.addin", "CadQC.addin") {
        $man = Join-Path $addins $m
        if (Test-Path $man) { Remove-Item $man -Force; $removed++ }
    }
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Host "  removed from Revit $v" -ForegroundColor Green }
}
if ($removed -eq 0) { Write-Host "  Nothing was installed." }
Read-Host "  Press Enter to close"
