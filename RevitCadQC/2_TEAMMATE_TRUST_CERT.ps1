# =====================================================================
# FOR TEAMMATES - trusts the Revit CAD QC certificate so Revit stops asking
# Put RevitCadQCSign.cer in the SAME folder as this script (or in cert\), then
# double-click RUN_2_TEAMMATE_TRUST_CERT.bat. Click YES. No admin needed.
# (INSTALL_FOR_TEAMMATE in the team package already does this step.)
# Do NOT copy-paste this text into a console - run the FILE.
# =====================================================================
$ErrorActionPreference = "Continue"
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
$root = $PSScriptRoot
if (-not $root -and $MyInvocation.MyCommand.Path) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $root) {
    Write-Host "Run this as a FILE (double-click RUN_2_TEAMMATE_TRUST_CERT.bat), do not paste the text." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
$cer = @((Join-Path $root "RevitCadQCSign.cer"), (Join-Path $root "cert\RevitCadQCSign.cer")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cer) {
    Write-Host "RevitCadQCSign.cer not found next to this script (or in cert\)." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root -ErrorAction Stop | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher -ErrorAction Stop | Out-Null
Write-Host "DONE. Publisher 'RevitCadQC Code Signing' is now trusted." -ForegroundColor Green
Read-Host "Press Enter to close"
