<#
====================================================================
 REVIT CAD QC - MAKE TEAM PACKAGE  (run RUN_4_MAKE_TEAM_PACKAGE.bat)
====================================================================
 Creates  Desktop\RevitCadQC_TEAM_PACKAGE  (and a .zip of it) with:
   R2022\ R2025\ ...          signed DLLs for every version you built
   CadQC.addin                manifest
   CadLayerDictionary.txt     your layer rules (if edited)
   CadProfiles\               confirmed consultant layer profiles
   RevitCadQC_CodeSigning.cer certificate
   qc-settings.default.json   office layer standard + tolerances
   INSTALL_FOR_TEAMMATE.BAT   teammates DOUBLE-CLICK this
   INSTALL_FOR_TEAMMATE.ps1
   READ ME FIRST.txt
 Teammates do NOT need the source folder, bin, obj or the .NET SDK.
 Run RUN_1_BUILD_AND_INSTALL first so the DLLs are built and signed.
====================================================================
#>
$ErrorActionPreference = "Continue"
trap {
    Write-Host ""
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "  Press Enter to close"
    exit 1
}
$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root) -and $MyInvocation.MyCommand.Path) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($Root) -or -not (Test-Path (Join-Path $Root "src\CadQC.Revit"))) {
    Write-Host "  Run this by double-clicking RUN_4_MAKE_TEAM_PACKAGE.bat in the RevitCadQC folder." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}

$bin = Join-Path $Root "src\CadQC.Revit\bin"
$built = Get-ChildItem $bin -Directory -Filter "R20*" -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName "CadQC.Revit.dll") }
if (-not $built) {
    Write-Host "  No build found in src\CadQC.Revit\bin\R20xx. Run RUN_1_BUILD_AND_INSTALL.bat first." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}
$cer = Join-Path $Root "cert\RevitCadQC_CodeSigning.cer"
if (-not (Test-Path $cer)) {
    Write-Host "  cert\RevitCadQC_CodeSigning.cer not found. Run RUN_1_BUILD_AND_INSTALL.bat first." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}

$desktop = [Environment]::GetFolderPath("Desktop")
$pkg = Join-Path $desktop "RevitCadQC_TEAM_PACKAGE"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

$versions = @()
foreach ($b in $built) {
    $sig = Get-AuthenticodeSignature (Join-Path $b.FullName "CadQC.Revit.dll")
    if ($sig.Status -ne "Valid") {
        Write-Host "  $($b.Name): DLL is not signed/valid ($($sig.Status)) - skipped. Run RUN_1_BUILD_AND_INSTALL.bat." -ForegroundColor Yellow
        continue
    }
    $dst = Join-Path $pkg $b.Name
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Get-ChildItem $b.FullName -File | Where-Object { $_.Name -ne "CadQC.addin" -and $_.Extension -ne ".log" } | Copy-Item -Destination $dst -Force
    $versions += $b.Name.Substring(1)
    Write-Host "  added $($b.Name)" -ForegroundColor Green
}
if ($versions.Count -eq 0) { Write-Host "  No signed build to package." -ForegroundColor Red; Read-Host "  Press Enter to close"; exit 1 }

Copy-Item (Join-Path $Root "src\CadQC.Revit\CadQC.addin") $pkg
Copy-Item $cer $pkg
if (Test-Path (Join-Path $Root "config\qc-settings.sample.json")) { Copy-Item (Join-Path $Root "config\qc-settings.sample.json") (Join-Path $pkg "qc-settings.default.json") }
# layer dictionary (your edited copy wins) + every confirmed consultant profile
$dictMine = Join-Path $env:APPDATA "RevitCadQC\CadLayerDictionary.txt"
if (Test-Path $dictMine) { Copy-Item $dictMine (Join-Path $pkg "CadLayerDictionary.txt") -Force }
$profSrc = Join-Path $env:APPDATA "RevitCadQC\CadProfiles"
if (Test-Path $profSrc) {
    New-Item -ItemType Directory -Force -Path (Join-Path $pkg "CadProfiles") | Out-Null
    Copy-Item (Join-Path $profSrc "*.json") (Join-Path $pkg "CadProfiles") -Force -ErrorAction SilentlyContinue
    Write-Host "  added $((Get-ChildItem (Join-Path $pkg 'CadProfiles') -Filter *.json).Count) consultant profile(s)" -ForegroundColor Green
}
# the build PC's own default settings (office layer standard) win over the sample
$mine = Join-Path $env:APPDATA "RevitCadQC\default_settings.json"
if (Test-Path $mine) { Copy-Item $mine (Join-Path $pkg "qc-settings.default.json") -Force }

# ---------------- teammate installer ----------------
$installer = @'
# REVIT CAD QC - INSTALL FOR TEAMMATE (double-click INSTALL_FOR_TEAMMATE.BAT)
$ErrorActionPreference = "Continue"
trap { Write-Host ""; Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red; Read-Host "  Press Enter to close"; exit 1 }
$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root) -and $MyInvocation.MyCommand.Path) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($Root) -or -not (Test-Path (Join-Path $Root "CadQC.addin"))) {
    Write-Host "  Double-click INSTALL_FOR_TEAMMATE.BAT inside the RevitCadQC_TEAM_PACKAGE folder." -ForegroundColor Red
    Write-Host "  Do not paste this text into a PowerShell window." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Host "  Close Revit first - it locks the add-in DLL." -ForegroundColor Yellow
    Read-Host "  Press Enter to close"; exit 1
}

# 1. trust the certificate
$cer = Join-Path $Root "RevitCadQC_CodeSigning.cer"
$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
foreach ($store in @("Root", "TrustedPublisher")) {
    if (-not (Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object { $_.Thumbprint -eq $c.Thumbprint })) {
        if ($store -eq "Root") { Write-Host "  Windows will ask to trust the certificate - click YES." -ForegroundColor Yellow }
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
    }
}
Write-Host "  certificate trusted" -ForegroundColor Green

# 2. install every packaged version that is installed on this PC (or all if Revit is not found)
$folders = Get-ChildItem $Root -Directory -Filter "R20*"
$any = $false
foreach ($f in $folders) {
    $v = $f.Name.Substring(1)
    $hasRevit = (Test-Path "$env:ProgramFiles\Autodesk\Revit $v\Revit.exe") -or (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$v")
    if (-not $hasRevit) { Write-Host "  Revit $v not found on this PC - skipped"; continue }
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $dst = Join-Path $addins "RevitCadQC"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    foreach ($file in Get-ChildItem $f.FullName -File) {
        Copy-Item $file.FullName (Join-Path $dst $file.Name) -Force -ErrorAction Stop
        if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $dst $file.Name)).Hash) { throw "copy check failed for $($file.Name)" }
    }
    Copy-Item (Join-Path $Root "CadQC.addin") (Join-Path $addins "CadQC.addin") -Force -ErrorAction Stop
    $sig = Get-AuthenticodeSignature (Join-Path $dst "CadQC.Revit.dll")
    Write-Host "  installed for Revit $v  (hash verified, signature $($sig.Status))" -ForegroundColor Green
    $any = $true
}
if (-not $any) { Write-Host "  No matching Revit version found on this PC." -ForegroundColor Yellow }

# 3. office default settings (never overwrites a teammate's own)
$sd = Join-Path $env:APPDATA "RevitCadQC"
New-Item -ItemType Directory -Force -Path $sd | Out-Null
$def = Join-Path $Root "qc-settings.default.json"
if ((Test-Path $def) -and -not (Test-Path (Join-Path $sd "default_settings.json"))) { Copy-Item $def (Join-Path $sd "default_settings.json") }
# layer dictionary (only if the teammate has none) + consultant profiles (never overwrite their own)
$dict = Join-Path $Root "CadLayerDictionary.txt"
if ((Test-Path $dict) -and -not (Test-Path (Join-Path $sd "CadLayerDictionary.txt"))) { Copy-Item $dict (Join-Path $sd "CadLayerDictionary.txt") }
$pp = Join-Path $Root "CadProfiles"
if (Test-Path $pp) {
    $dstP = Join-Path $sd "CadProfiles"
    New-Item -ItemType Directory -Force -Path $dstP | Out-Null
    foreach ($pf in Get-ChildItem $pp -Filter *.json) { if (-not (Test-Path (Join-Path $dstP $pf.Name))) { Copy-Item $pf.FullName $dstP } }
    Write-Host "  consultant profiles installed" -ForegroundColor Green
}

# 4. DWG files are read by the built-in reader (ACadSharp) - nothing else to install

Write-Host ""
Write-Host "  DONE. Start Revit -> tab 'CAD QC'." -ForegroundColor Green
Read-Host "  Press Enter to close"
'@
Set-Content -Path (Join-Path $pkg "INSTALL_FOR_TEAMMATE.ps1") -Value $installer -Encoding ASCII
Set-Content -Path (Join-Path $pkg "INSTALL_FOR_TEAMMATE.BAT") -Encoding ASCII -Value @(
    "@echo off",
    "powershell -NoProfile -ExecutionPolicy Bypass -File ""%~dp0INSTALL_FOR_TEAMMATE.ps1"""
)

$readme = @"
REVIT CAD QC - TEAM PACKAGE
===========================
Built $(Get-Date -Format "yyyy-MM-dd HH:mm") on $env:COMPUTERNAME for Revit $($versions -join ", ").

INSTALL
  1. Close Revit.
  2. DOUBLE-CLICK  INSTALL_FOR_TEAMMATE.BAT
     (do not open the .ps1 in PowerShell and paste it - run the .bat)
  3. When Windows asks to trust "RevitCadQC Code Signing", click YES.
  4. (Optional) ODA File Converter - only a fallback for unusual DWGs:
     https://www.opendesign.com/guestfiles/oda_file_converter
  5. Start Revit -> tab "CAD QC".

USE
  Run CAD QC -> pick the folder with the CAD plans -> Run QC.
  Results: QC plans in Revit (clouds + linked CAD), Issue Browser,
  QC_Report.html, QC_Issues.xlsx and CAD_Markup\*.scr for AutoCAD
  (open the DWG, type SCRIPT, pick the .scr).

UPDATE
  Get the new package and double-click INSTALL_FOR_TEAMMATE.BAT again.

UNINSTALL
  Delete %APPDATA%\Autodesk\Revit\Addins\<version>\RevitCadQC and
  %APPDATA%\Autodesk\Revit\Addins\<version>\CadQC.addin
"@
Set-Content -Path (Join-Path $pkg "READ ME FIRST.txt") -Value $readme -Encoding ASCII

$zip = Join-Path $desktop "RevitCadQC_TEAM_PACKAGE.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pkg "*") -DestinationPath $zip

Write-Host ""
Write-Host "====================================================================" -ForegroundColor Green
Write-Host "  Team package: $pkg" -ForegroundColor Green
Write-Host "  Zip:          $zip" -ForegroundColor Green
Write-Host "  Teammates double-click INSTALL_FOR_TEAMMATE.BAT. Done." -ForegroundColor Green
Write-Host "====================================================================" -ForegroundColor Green
Read-Host "  Press Enter to close"
