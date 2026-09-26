<#
====================================================================
 REVIT CAD QC - BUILD AND INSTALL  (run RUN_1_BUILD_AND_INSTALL.bat)
====================================================================
 ONE script, run after every change:
   1. refuses to run while Revit is open (Revit locks the installed DLL)
   2. finds the .NET 8 SDK, or installs a user-local one (no admin)
   3. runs the unit tests
   4. builds every Revit version installed on this PC (2022-2026)
   5. creates / reuses the code-signing certificate and trusts it
   6. signs the DLLs
   7. installs into %APPDATA%\Autodesk\Revit\Addins\<version>\RevitCadQC
   8. verifies every copied file by SHA256 hash + signature
   9. reports the optional DWG fallback converter (DWG is read built-in)

 Options:  -Versions 2022,2025   build only these
           -SkipTests            skip the unit tests
====================================================================
#>
param(
    [string[]]$Versions,
    [switch]$SkipTests
)

# Continue, not Stop: Windows PowerShell 5.1 turns any stderr line of a native tool (dotnet) into a
# terminating error under Stop. Every step is checked explicitly instead.
$ErrorActionPreference = "Continue"
trap {
    Write-Host ""
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "  Press Enter to close"
    exit 1
}
$CertSubject = "CN=RevitCadQC Code Signing"
$AddinFolderName = "RevitCadQC"

# ---------- root (never trust $PSScriptRoot alone: empty when the text is pasted into a console) ----------
$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root) -and $MyInvocation.MyCommand.Path) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($Root) -or -not (Test-Path (Join-Path $Root "src\CadQC.Revit\CadQC.Revit.csproj"))) {
    Write-Host ""
    Write-Host "  Could not find the source folder." -ForegroundColor Red
    Write-Host "  Run this by DOUBLE-CLICKING RUN_1_BUILD_AND_INSTALL.bat in the RevitCadQC folder." -ForegroundColor Red
    Write-Host "  Do not paste the script text into a PowerShell window." -ForegroundColor Red
    Read-Host "  Press Enter to close"
    exit 1
}
Set-Location $Root

function Step($t) { Write-Host ""; Write-Host "==== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host ""; Write-Host "  FAILED: $t" -ForegroundColor Red; Read-Host "  Press Enter to close"; exit 1 }
function Ok($t)   { Write-Host "  OK  $t" -ForegroundColor Green }

# ---------- 1. Revit must be closed ----------
Step "Checking Revit is closed"
$revit = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revit) {
    Write-Host "  Revit is running. It locks the installed add-in DLL, so the new build could not be copied." -ForegroundColor Yellow
    Write-Host "  Close every Revit window and run this again." -ForegroundColor Yellow
    Write-Host "  (To build without installing while Revit is open, use RUN_BUILD_ONLY_R2025.bat.)" -ForegroundColor Yellow
    Read-Host "  Press Enter to close"
    exit 1
}
Ok "Revit is not running"

# ---------- 2. dotnet SDK ----------
Step "Finding the .NET 8 SDK"
function Find-Dotnet {
    $candidates = @(
        "$env:USERPROFILE\.dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:USERPROFILE\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match "^(8|9|10)\.") { return $c }
        }
    }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $sdks = & $onPath.Source --list-sdks 2>$null
        if ($sdks -match "^(8|9|10)\.") { return $onPath.Source }
    }
    return $null
}
$dotnet = Find-Dotnet
if (-not $dotnet) {
    Write-Host "  .NET 8 SDK not found. Installing a user-local copy (no admin rights needed)..." -ForegroundColor Yellow
    $installDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $script = Join-Path $env:TEMP "dotnet-install.ps1"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing "https://dot.net/v1/dotnet-install.ps1" -OutFile $script
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $installDir
    } catch { Fail "Could not download the .NET SDK: $($_.Exception.Message). Install it from https://dotnet.microsoft.com/download" }
    $dotnet = Find-Dotnet
    if (-not $dotnet) { Fail ".NET SDK install did not work. Install it from https://dotnet.microsoft.com/download" }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
Ok "dotnet: $dotnet"

# ---------- 3. which Revit versions ----------
Step "Choosing Revit versions"
$supported = @("2022", "2023", "2024", "2025", "2026")
if (-not $Versions -or $Versions.Count -eq 0) {
    $Versions = @()
    foreach ($v in $supported) {
        if ((Test-Path "$env:ProgramFiles\Autodesk\Revit $v\Revit.exe") -or (Test-Path "$env:APPDATA\Autodesk\Revit\Addins\$v")) { $Versions += $v }
    }
    if ($Versions.Count -eq 0) { $Versions = @("2022", "2025") }
}
$Versions = $Versions | ForEach-Object { "$_".Trim().TrimStart("R", "r") } | Where-Object { $supported -contains $_ }
if ($Versions.Count -eq 0) { Fail "No supported Revit version given (use 2022, 2023, 2024, 2025 or 2026)." }
Ok ("Revit " + ($Versions -join ", "))

# ---------- 4. tests ----------
if (-not $SkipTests) {
    Step "Running unit tests"
    & $dotnet test "tests\CadQC.Tests\CadQC.Tests.csproj" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "Unit tests failed - nothing was installed." }
    Ok "all tests passed"
}

# ---------- 5. build ----------
foreach ($v in $Versions) {
    Step "Building for Revit $v"
    $log = Join-Path $Root "build_R$v.log"
    & $dotnet build "src\CadQC.Revit\CadQC.Revit.csproj" -c "R$v" --nologo -v q | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { Fail "Build for Revit $v failed. See $log" }
    if (-not (Test-Path "src\CadQC.Revit\bin\R$v\CadQC.Revit.dll")) { Fail "Build output missing for Revit $v" }
    Ok "bin\R$v\CadQC.Revit.dll"
}

# ---------- 6. certificate ----------
Step "Code-signing certificate"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "  Creating a new self-signed code-signing certificate (valid 5 years)..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
    if (-not $cert) { Fail "Could not create the code-signing certificate." }
}
$certDir = Join-Path $Root "cert"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
$cerFile = Join-Path $certDir "RevitCadQC_CodeSigning.cer"
Export-Certificate -Cert $cert -FilePath $cerFile -Force | Out-Null
foreach ($store in @("Root", "TrustedPublisher")) {
    $have = Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if (-not $have) {
        if ($store -eq "Root") { Write-Host "  Windows will ask once to trust the certificate - click YES." -ForegroundColor Yellow }
        Import-Certificate -FilePath $cerFile -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
    }
}
Ok "certificate $($cert.Thumbprint) (exported to cert\RevitCadQC_CodeSigning.cer)"

# ---------- 7. sign ----------
Step "Signing DLLs"
function Sign-File($path) {
    $r = $null
    try { $r = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com" }
    catch { $r = $null }
    if (-not $r -or $r.Status -ne "Valid") {
        # offline: sign without a timestamp
        $r = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256
    }
    if ($r.Status -ne "Valid") { Fail "Signing failed for $path : $($r.StatusMessage)" }
}
foreach ($v in $Versions) {
    foreach ($dll in @("CadQC.Revit.dll", "CadQC.Core.dll")) { Sign-File (Join-Path $Root "src\CadQC.Revit\bin\R$v\$dll") }
    Ok "R$v signed"
}

# ---------- 8. install + 9. verify ----------
foreach ($v in $Versions) {
    Step "Installing for Revit $v"
    $src = Join-Path $Root "src\CadQC.Revit\bin\R$v"
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $dst = Join-Path $addins $AddinFolderName
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    $files = Get-ChildItem $src -File | Where-Object { $_.Name -ne "CadQC.addin" }
    foreach ($f in $files) { Copy-Item $f.FullName (Join-Path $dst $f.Name) -Force -ErrorAction Stop }
    Copy-Item (Join-Path $src "CadQC.addin") (Join-Path $addins "CadQC.addin") -Force -ErrorAction Stop

    foreach ($f in $files) {
        $a = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        $b = (Get-FileHash (Join-Path $dst $f.Name) -Algorithm SHA256).Hash
        if ($a -ne $b) { Fail "Copy check failed for $($f.Name) (Revit $v). Is Revit or another program holding the file?" }
    }
    $sig = Get-AuthenticodeSignature (Join-Path $dst "CadQC.Revit.dll")
    if ($sig.Status -ne "Valid") { Fail "Installed DLL signature is $($sig.Status) for Revit $v" }
    Ok "$dst  (hash + signature verified)"
}

# ---------- office default settings ----------
$settingsDir = Join-Path $env:APPDATA "RevitCadQC"
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
$defaults = Join-Path $settingsDir "default_settings.json"
if (-not (Test-Path $defaults) -and (Test-Path "config\qc-settings.sample.json")) {
    Copy-Item "config\qc-settings.sample.json" $defaults
    Ok "default settings -> $defaults"
}

# ---------- DWG converter ----------
Step "DWG reading"
$oda = Get-ChildItem "$env:ProgramFiles\ODA" -Recurse -Filter "ODAFileConverter.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
$acad = Get-ChildItem "$env:ProgramFiles\Autodesk" -Directory -Filter "AutoCAD*" -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "accoreconsole.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($oda) { Ok "ODA File Converter: $($oda.FullName)" }
elseif ($acad) { Ok "AutoCAD Core Console: $acad" }
else { Ok "DWG files are read by the built-in reader (ACadSharp). ODA File Converter is an optional fallback." }

Write-Host ""
Write-Host "====================================================================" -ForegroundColor Green
Write-Host "  DONE. Start Revit -> tab 'CAD QC' -> Run CAD QC." -ForegroundColor Green
Write-Host "  Give it to the team: RUN_4_MAKE_TEAM_PACKAGE.bat" -ForegroundColor Green
Write-Host "====================================================================" -ForegroundColor Green
Read-Host "  Press Enter to close"
