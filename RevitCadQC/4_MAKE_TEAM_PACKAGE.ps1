# =====================================================================
# MAKE TEAM PACKAGE - run AFTER RUN_1_BUILD_AND_INSTALL   (double-click RUN_4_MAKE_TEAM_PACKAGE.bat)
# Creates "RevitCadQC_TEAM_PACKAGE" (+ .zip) on your Desktop with everything a teammate needs:
#   R2022\ R2025\ ...          signed RevitCadQC.dll + ACadSharp + Newtonsoft (+ System.Memory for 2022-24)
#   RevitCadQC.addin           manifest
#   RevitCadQCSign.cer         certificate
#   version.txt                version, date, FORCE, change notes  (Check for Update / silent update)
#   CadQcConfig.txt            central office rules (read live from the package by every tool)
#   CadLayerDictionary.txt     layer rules (your edited copy if you have one)
#   CadProfiles\               every confirmed consultant layer profile
#   qc-settings.default.json   office default settings
#   INSTALL_FOR_TEAMMATE.bat   teammates DOUBLE-CLICK this - nothing else
#   READ ME FIRST.txt
# Then copies the package to every folder in TEAM_DEPLOY_PATHS.txt (network drives).
#
#   -NoForce   team gets a popup "new version available" instead of a silent update
# =====================================================================
param([switch]$NoForce)
$ErrorActionPreference = "Continue"
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "At: $($_.InvocationInfo.PositionMessage)" -ForegroundColor DarkYellow
    Read-Host "Press Enter to close"; exit 1
}
$root = $PSScriptRoot
if (-not $root -and $MyInvocation.MyCommand.Path) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $root -or -not (Test-Path (Join-Path $root "RevitCadQC.csproj"))) {
    Write-Host "DOUBLE-CLICK RUN_4_MAKE_TEAM_PACKAGE.bat in the RevitCadQC folder - do not paste this text." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
Set-Location $root

function Fail($t) { Write-Host ""; Write-Host "  $t" -ForegroundColor Red; Write-Host "  The team folder was NOT touched. Team keeps the current version." -ForegroundColor Yellow; Read-Host "Press Enter to close"; exit 1 }

# -- builds must exist, be signed and be fresh ------------------------------------
$verFile = "Core\ToolVersion.cs"
$srcTime = (Get-ChildItem -Path "Core", "Commands", "UI", "App.cs" -Recurse -Filter *.cs | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
$builds = @(Get-ChildItem "bin" -Directory -Filter "R20*" -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName "RevitCadQC.dll") })
if ($builds.Count -eq 0) { Fail "No build found in bin\R20xx. Run RUN_1_BUILD_AND_INSTALL.bat first." }
foreach ($b in $builds) {
    $dll = Join-Path $b.FullName "RevitCadQC.dll"
    if ((Get-Item $dll).LastWriteTime -lt $srcTime.AddMinutes(-2)) { Fail "STALE $($b.Name)\RevitCadQC.dll (older than the source). Run RUN_1_BUILD_AND_INSTALL.bat first." }
    $sig = Get-AuthenticodeSignature $dll
    if ($sig.Status -ne "Valid") { Fail "$($b.Name)\RevitCadQC.dll is not signed ($($sig.Status)). Run RUN_1_BUILD_AND_INSTALL.bat first." }
}
$cer = @((Join-Path $root "cert\RevitCadQCSign.cer"), (Join-Path $env:USERPROFILE "RevitCadQCSign.cer")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cer) { Fail "RevitCadQCSign.cer NOT FOUND. Run RUN_1_BUILD_AND_INSTALL.bat first." }

# -- package folder ----------------------------------------------------------------------
$pkg = Join-Path ([Environment]::GetFolderPath("Desktop")) "RevitCadQC_TEAM_PACKAGE"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pkg | Out-Null
foreach ($b in $builds) {
    $dst = Join-Path $pkg $b.Name
    New-Item -ItemType Directory -Force $dst | Out-Null
    Get-ChildItem $b.FullName -File | Where-Object { $_.Extension -notin ".log", ".pdb" -and $_.Name -ne "RevitCadQC.addin" } | Copy-Item -Destination $dst -Force
    Write-Host "  added $($b.Name)" -ForegroundColor Green
}
Copy-Item "RevitCadQC.addin" $pkg -Force
Copy-Item $cer (Join-Path $pkg "RevitCadQCSign.cer") -Force
Copy-Item "CadQcConfig.txt" $pkg -Force
$myDict = Join-Path $env:APPDATA "RevitCadQC\CadLayerDictionary.txt"
Copy-Item $(if (Test-Path $myDict) { $myDict } else { "CadLayerDictionary.txt" }) (Join-Path $pkg "CadLayerDictionary.txt") -Force
$myDefaults = Join-Path $env:APPDATA "RevitCadQC\default_settings.json"
Copy-Item $(if (Test-Path $myDefaults) { $myDefaults } else { "config\qc-settings.sample.json" }) (Join-Path $pkg "qc-settings.default.json") -Force
$profSrc = Join-Path $env:APPDATA "RevitCadQC\CadProfiles"
if (Test-Path $profSrc) {
    New-Item -ItemType Directory -Force (Join-Path $pkg "CadProfiles") | Out-Null
    Copy-Item (Join-Path $profSrc "*.json") (Join-Path $pkg "CadProfiles") -Force -ErrorAction SilentlyContinue
    Write-Host "  added $(@(Get-ChildItem (Join-Path $pkg 'CadProfiles') -Filter *.json).Count) consultant profile(s)" -ForegroundColor Green
}

# -- version.txt: version, date, FORCE, notes (read by Check for Update + Revit start) --------
$verText = Get-Content $verFile -Raw
$ver  = [regex]::Match($verText, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
$date = [regex]::Match($verText, 'UpdatedOn\s*=\s*"([^"]+)"').Groups[1].Value
$notes = @([regex]::Matches($verText, '"(- [^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$lines = @($ver, $date)
if (-not $NoForce) { $lines += "FORCE" }
$lines += $notes
$lines | Set-Content (Join-Path $pkg "version.txt") -Encoding ASCII
Write-Host "  version.txt: $ver ($date)$(if (-not $NoForce) { ' FORCE' })" -ForegroundColor Green

# -- teammate installer ---------------------------------------------------------------
@'
# =====================================================================
# TEAMMATE INSTALL - do NOT copy-paste this text into PowerShell.
# DOUBLE-CLICK "INSTALL_FOR_TEAMMATE.bat" (it runs this file properly).
# Click YES when the certificate dialog appears. No admin needed.
# =====================================================================
$ErrorActionPreference = "Continue"
trap { Write-Host ""; Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red; Read-Host "Press Enter to close"; exit 1 }
$root = $PSScriptRoot
if (-not $root -and $MyInvocation.MyCommand.Path) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $root -or -not (Test-Path (Join-Path $root "RevitCadQC.addin"))) {
    Write-Host "Run the .BAT file inside the RevitCadQC_TEAM_PACKAGE folder - do not paste this text into a console." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host "Close ALL Revit windows first - Revit locks the add-in DLL." -ForegroundColor Yellow
    Read-Host "Press Enter to close"; exit 1
}

# 1. certificate
$cer = Join-Path $root "RevitCadQCSign.cer"
if (Test-Path $cer) {
    $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
    foreach ($store in "Root", "TrustedPublisher") {
        if (-not (Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object Thumbprint -eq $c.Thumbprint)) {
            if ($store -eq "Root") { Write-Host "Trusting the publisher - CLICK YES on the dialog..." -ForegroundColor Yellow }
            Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
        }
    }
    Write-Host "Publisher trusted." -ForegroundColor Green
} else { Write-Host "RevitCadQCSign.cer missing - Revit will show an unsigned-add-in prompt." -ForegroundColor Yellow }

# 2. add-in for every packaged Revit version installed on this PC
$failed = $false; $any = $false
foreach ($f in Get-ChildItem $root -Directory -Filter "R20*") {
    $v = $f.Name.Substring(1)
    if (-not ((Test-Path "$env:ProgramFiles\Autodesk\Revit $v\Revit.exe") -or (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$v"))) {
        Write-Host "SKIP Revit $v - not installed on this PC"; continue
    }
    $addins = "$env:APPDATA\Autodesk\Revit\Addins\$v"
    $dst = Join-Path $addins "RevitCadQC"
    if ((Test-Path $dst) -and -not (Get-Item $dst).PSIsContainer) { Remove-Item $dst -Force }
    New-Item -ItemType Directory -Force $dst | Out-Null
    Remove-Item (Join-Path $addins "CadQC.addin") -Force -ErrorAction SilentlyContinue
    foreach ($file in Get-ChildItem $f.FullName -File) {
        Copy-Item $file.FullName (Join-Path $dst $file.Name) -Force
        if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $dst $file.Name)).Hash) {
            Write-Host "COPY VERIFY FAILED: $($file.Name) for Revit $v" -ForegroundColor Red; $failed = $true
        }
    }
    Copy-Item (Join-Path $root "RevitCadQC.addin") (Join-Path $addins "RevitCadQC.addin") -Force
    $sig = Get-AuthenticodeSignature (Join-Path $dst "RevitCadQC.dll")
    Write-Host "Installed for Revit $v  (signature $($sig.Status))" -ForegroundColor Green
    $any = $true
}
if (-not $any) { Write-Host "No matching Revit version found on this PC." -ForegroundColor Yellow }

# 3. shared data: settings, dictionary, profiles (never overwrites the teammate's own)
$app = Join-Path $env:APPDATA "RevitCadQC"
New-Item -ItemType Directory -Force $app | Out-Null
if (-not (Test-Path "$app\default_settings.json") -and (Test-Path "$root\qc-settings.default.json")) { Copy-Item "$root\qc-settings.default.json" "$app\default_settings.json" }
if (-not (Test-Path "$app\CadLayerDictionary.txt") -and (Test-Path "$root\CadLayerDictionary.txt")) { Copy-Item "$root\CadLayerDictionary.txt" "$app\CadLayerDictionary.txt" }
if (Test-Path "$root\CadQcConfig.txt") { Copy-Item "$root\CadQcConfig.txt" "$app\CadQcConfig.txt" -Force }   # offline fallback copy
if (Test-Path "$root\CadProfiles") {
    New-Item -ItemType Directory -Force "$app\CadProfiles" | Out-Null
    foreach ($p in Get-ChildItem "$root\CadProfiles" -Filter *.json) { if (-not (Test-Path "$app\CadProfiles\$($p.Name)")) { Copy-Item $p.FullName "$app\CadProfiles" } }
}

# 4. remember where updates come from (network package folder), for CHECK FOR UPDATE
$source = $null
if (Test-Path "$root\package_source.txt") {
    $source = Get-Content "$root\package_source.txt" | Where-Object { $_.Trim() -and (Test-Path $_.Trim()) } | Select-Object -First 1
}
if (-not $source) { $source = $root }
Set-Content "$app\package_path.txt" $source.Trim() -Encoding ASCII
Write-Host "Updates will come from: $source" -ForegroundColor DarkGray

Write-Host ""
if ($failed) { Write-Host "FINISHED WITH ERRORS - see above." -ForegroundColor Red }
else { Write-Host "DONE. Open Revit - the CAD QC tab is ready." -ForegroundColor Green }
Read-Host "Press Enter to close"
'@ | Set-Content (Join-Path $pkg "INSTALL_FOR_TEAMMATE.ps1") -Encoding ASCII

# BAT launcher: bypasses execution policy and always runs the ps1 as a FILE
@'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0INSTALL_FOR_TEAMMATE.ps1"
'@ | Set-Content (Join-Path $pkg "INSTALL_FOR_TEAMMATE.bat") -Encoding ASCII

@"
REVIT CAD QC $ver - TEAM PACKAGE
================================
HOW TO INSTALL (teammate):
1. Copy this WHOLE folder to your PC (do not run it from the network drive directly).
2. Close all Revit windows.
3. DOUBLE-CLICK  INSTALL_FOR_TEAMMATE.bat
4. Click YES on the certificate dialog.
5. Open Revit - the CAD QC tab is ready.

DO NOT copy-paste script text into a PowerShell window - that breaks the paths.
Always run the .bat file.

UPDATES: Revit tells you at start-up when a newer version is in the team folder
(or updates silently). You can also click CHECK FOR UPDATE on the CAD QC tab.

WHAT'S NEW:
$($notes -join "`r`n")
"@ | Set-Content (Join-Path $pkg "READ ME FIRST.txt") -Encoding ASCII

# -- deploy to network folders --------------------------------------------------------
$deploy = @()
if (Test-Path "TEAM_DEPLOY_PATHS.txt") { $deploy = @(Get-Content "TEAM_DEPLOY_PATHS.txt" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith("#") }) }
if ($deploy.Count -gt 0) { $deploy | Set-Content (Join-Path $pkg "package_source.txt") -Encoding ASCII }

# strip Mark-of-the-Web so teammates' PCs do not block the files
Get-ChildItem $pkg -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$zip = Join-Path ([Environment]::GetFolderPath("Desktop")) "RevitCadQC_TEAM_PACKAGE.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pkg "*") -DestinationPath $zip
Write-Host "Package created: $pkg" -ForegroundColor Green
Write-Host "Zip:             $zip" -ForegroundColor Green

foreach ($d in $deploy) {
    try {
        New-Item -ItemType Directory -Force $d -ErrorAction Stop | Out-Null
        Copy-Item (Join-Path $pkg "*") $d -Recurse -Force -ErrorAction Stop
        Write-Host "DEPLOYED to: $d" -ForegroundColor Green
    } catch {
        Write-Host "Could not deploy to $d : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if ($deploy.Count -eq 0) { Write-Host "No deploy folders in TEAM_DEPLOY_PATHS.txt - share the Desktop folder / zip yourself." -ForegroundColor Yellow }
else { Write-Host "Team members: restart Revit (FORCE = silent update) or click CHECK FOR UPDATE." -ForegroundColor Yellow }
Write-Host "New teammates: DOUBLE-CLICK INSTALL_FOR_TEAMMATE.bat from the package folder." -ForegroundColor Yellow
Read-Host "Press Enter to close"
