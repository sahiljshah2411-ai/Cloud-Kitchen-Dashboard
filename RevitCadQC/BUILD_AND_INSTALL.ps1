# =====================================================================
# REVIT CAD QC - ONE SCRIPT DOES EVERYTHING   (double-click RUN_1_BUILD_AND_INSTALL.bat)
#
# Close ALL Revit windows first. Then:
#   1. blocks if Revit is running (prevents stale locked-DLL copies)
#   2. finds a dotnet that really has an SDK (installs user-local .NET 8 if missing - no admin)
#   3. runs the unit tests (skip with -SkipTests)
#   4. builds every Revit version installed on this PC (R2022..R2026) - or -Versions 2022,2025
#   5. creates the code-signing certificate if missing (one time, click YES) and trusts it
#   6. signs RevitCadQC.dll
#   7. installs DLLs + .addin + dictionary into %APPDATA%\Autodesk\Revit\Addins\<ver>\RevitCadQC
#   8. verifies every copied file by hash and shows the signature status
# =====================================================================
param(
    [string[]]$Versions,
    [switch]$SkipTests
)
# Continue (not Stop): Windows PowerShell 5.1 turns any stderr line of dotnet into a terminating
# error under Stop. Every step is checked explicitly instead.
$ErrorActionPreference = "Continue"

# any unhandled error: SHOW it and pause - the window must never close silently
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "At: $($_.InvocationInfo.PositionMessage)" -ForegroundColor DarkYellow
    Read-Host "Press Enter to close"; exit 1
}

# $PSScriptRoot is EMPTY when the text is pasted into a console instead of run as a file
$root = $PSScriptRoot
if (-not $root -and $MyInvocation.MyCommand.Path) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $root -or -not (Test-Path (Join-Path $root "RevitCadQC.csproj"))) {
    Write-Host "Could not find RevitCadQC.csproj next to this script." -ForegroundColor Red
    Write-Host "DOUBLE-CLICK RUN_1_BUILD_AND_INSTALL.bat in the RevitCadQC folder - do not paste this text into PowerShell." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
Set-Location $root

$CertSubject   = "CN=RevitCadQC Code Signing"
$AddinName     = "RevitCadQC"
$Supported     = @("2022", "2023", "2024", "2025", "2026")

function Step($t) { Write-Host ""; Write-Host "==== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  OK  $t" -ForegroundColor Green }
function Fail($t) { Write-Host ""; Write-Host "  FAILED: $t" -ForegroundColor Red; Read-Host "Press Enter to close"; exit 1 }

# -- 1. Revit must be closed -------------------------------------------
Step "Revit must be closed"
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Fail "REVIT IS RUNNING. Close ALL Revit windows and run this again. (Build only, Revit open: RUN_BUILD_ONLY_R2025.bat)"
}
Ok "Revit is not running"

# -- 2. dotnet SDK - robust finder ----------------------------------------
# Some PCs have a runtime-only C:\Program Files\dotnet that hijacks 'dotnet' but has no SDK.
Step "Finding a .NET SDK"
function Find-DotnetWithSdk {
    $candidates = @(
        (Get-ChildItem "$env:USERPROFILE\dotnet-sdk-*-win-x64\dotnet.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName),
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:USERPROFILE\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    foreach ($c in $candidates) {
        $sdks = & $c --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match "^(8|9|10)\.")) { return $c }
    }
    return $null
}
$dotnet = Find-DotnetWithSdk
if (-not $dotnet) {
    Write-Host "  No .NET 8+ SDK found - installing a user-local .NET 8 SDK (no admin)..." -ForegroundColor Yellow
    $local = "$env:LOCALAPPDATA\Microsoft\dotnet"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing "https://dot.net/v1/dotnet-install.ps1" -OutFile "$env:TEMP\dotnet-install.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir $local
    $dotnet = Find-DotnetWithSdk
    if (-not $dotnet) { Fail ".NET SDK install failed. Install it from https://dotnet.microsoft.com/download and run again." }
}
$dotnetDir = Split-Path -Parent $dotnet
$env:PATH = "$dotnetDir;" + $env:PATH
$env:DOTNET_ROOT = $dotnetDir
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
Ok ("dotnet " + (& $dotnet --version) + "  ($dotnet)")

# -- 3. which Revit versions --------------------------------------------
Step "Revit versions"
if (-not $Versions -or $Versions.Count -eq 0) {
    $Versions = @($Supported | Where-Object { (Test-Path "$env:ProgramFiles\Autodesk\Revit $_\Revit.exe") -or (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$_") })
    if ($Versions.Count -eq 0) { $Versions = @("2022", "2025") }
}
$Versions = @($Versions | ForEach-Object { ("$_" -split ",") } | ForEach-Object { $_.Trim().TrimStart("R", "r") } | Where-Object { $Supported -contains $_ } | Select-Object -Unique)
if ($Versions.Count -eq 0) { Fail "No supported Revit version (2022, 2023, 2024, 2025, 2026)." }
Ok ("building for Revit " + ($Versions -join ", "))

# -- 4a. tests ------------------------------------------------------------
if (-not $SkipTests) {
    Step "Unit tests"
    & $dotnet test "Tests\RevitCadQC.Tests.csproj" -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail "UNIT TESTS FAILED - nothing was installed. Fix the errors above." }
    Ok "all tests passed"
}

# -- 4b. build -----------------------------------------------------------
foreach ($v in $Versions) {
    Step "Building R$v"
    & $dotnet build "RevitCadQC.csproj" -c "R$v" -v q --nologo | Tee-Object -FilePath (Join-Path $root "build_R$v.log")
    if ($LASTEXITCODE -ne 0) { Fail "R$v BUILD FAILED - fix the errors above (log: build_R$v.log)." }
    if (-not (Test-Path "bin\R$v\RevitCadQC.dll")) { Fail "Missing build output bin\R$v\RevitCadQC.dll" }
    Ok "bin\R$v\RevitCadQC.dll"
}

# -- 5. certificate (create once + trust) -------------------------------
Step "Code-signing certificate"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "  Creating code-signing certificate (one time, valid 5 years)..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
    if (-not $cert) { Fail "Could not create the certificate." }
}
New-Item -ItemType Directory -Force "cert" | Out-Null
$cer = Join-Path $root "cert\RevitCadQCSign.cer"
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
Copy-Item $cer (Join-Path $env:USERPROFILE "RevitCadQCSign.cer") -Force
foreach ($store in "Root", "TrustedPublisher") {
    if (-not (Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        if ($store -eq "Root") { Write-Host "  Trusting certificate - CLICK YES on the dialog..." -ForegroundColor Yellow }
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
    }
}
Ok "certificate $($cert.Thumbprint)  (share cert\RevitCadQCSign.cer with teammates)"

# -- 6. sign --------------------------------------------------------------
Step "Signing"
foreach ($v in $Versions) {
    $p = "bin\R$v\RevitCadQC.dll"
    $r = Set-AuthenticodeSignature -FilePath $p -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com" -ErrorAction SilentlyContinue
    # the timestamp server fails offline - retry without timestamp
    if (-not $r -or $r.Status -ne "Valid") { $r = Set-AuthenticodeSignature -FilePath $p -Certificate $cert -HashAlgorithm SHA256 }
    if ($r.Status -ne "Valid") { Fail "Signing failed for $p : $($r.StatusMessage)" }
    Ok "$p signed"
}

# -- 7. install + 8. verify ------------------------------------------------
foreach ($v in $Versions) {
    Step "Installing for Revit $v"
    $addins = "$env:APPDATA\Autodesk\Revit\Addins\$v"
    $dstDir = Join-Path $addins $AddinName
    # the classic trap: a FILE named RevitCadQC where the folder should be
    if ((Test-Path $dstDir) -and -not (Get-Item $dstDir).PSIsContainer) { Remove-Item $dstDir -Force }
    New-Item -ItemType Directory -Force $dstDir | Out-Null
    # remove the manifest of the older CadQC.* layout so Revit does not load two copies
    Remove-Item (Join-Path $addins "CadQC.addin") -Force -ErrorAction SilentlyContinue
    Get-ChildItem $dstDir -Filter "CadQC.*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # copy EVERY file: RevitCadQC.dll needs ACadSharp.dll + Newtonsoft.Json.dll (+ System.Memory on 2022-2024).
    # Copying only the main DLL makes Revit fail at load time with an error that does not name the missing file.
    $files = Get-ChildItem "bin\R$v" -File | Where-Object { $_.Extension -ne ".log" -and $_.Name -ne "RevitCadQC.addin" }
    foreach ($f in $files) { Copy-Item $f.FullName (Join-Path $dstDir $f.Name) -Force -ErrorAction Stop }
    Copy-Item "bin\R$v\RevitCadQC.addin" (Join-Path $addins "RevitCadQC.addin") -Force -ErrorAction Stop

    foreach ($f in $files) {
        if ((Get-FileHash $f.FullName).Hash -ne (Get-FileHash (Join-Path $dstDir $f.Name)).Hash) {
            Fail "COPY VERIFY FAILED for $($f.Name), Revit $v (locked file?)"
        }
    }
    $sig = Get-AuthenticodeSignature (Join-Path $dstDir "RevitCadQC.dll")
    $ts  = (Get-Item (Join-Path $dstDir "RevitCadQC.dll")).LastWriteTime
    Write-Host ("  Revit {0}: RevitCadQC.dll + {1} other file(s) | copied {2} | signature {3}" -f $v, ($files.Count - 1), $ts, $sig.Status) -ForegroundColor Green
    if ($sig.Status -ne "Valid") { Fail "Installed DLL for Revit $v is NOT signed." }
}

# -- office defaults (first install only) -------------------------------------
$appData = Join-Path $env:APPDATA "RevitCadQC"
New-Item -ItemType Directory -Force $appData | Out-Null
if (-not (Test-Path "$appData\default_settings.json") -and (Test-Path "config\qc-settings.sample.json")) {
    Copy-Item "config\qc-settings.sample.json" "$appData\default_settings.json"
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host " ALL DONE - build + sign + install verified." -ForegroundColor Green
Write-Host " Open Revit -> tab 'CAD QC'." -ForegroundColor Green
Write-Host " DWG is read built-in (ACadSharp) - nothing else to install." -ForegroundColor Green
Write-Host " Give it to the team: RUN_4_MAKE_TEAM_PACKAGE.bat" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Read-Host "Press Enter to close"
