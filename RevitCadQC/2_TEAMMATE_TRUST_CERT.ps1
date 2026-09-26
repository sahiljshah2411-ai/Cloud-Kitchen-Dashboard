<#
====================================================================
 REVIT CAD QC - TRUST THE CODE-SIGNING CERTIFICATE
====================================================================
 Run once on a teammate's PC (RUN_2_TEAMMATE_TRUST_CERT.bat) so Revit
 loads the signed add-in without the "unsigned add-in" warning.
 Looks for RevitCadQC_CodeSigning.cer next to this script or in cert\.
 No admin rights needed (current-user certificate stores).
 INSTALL_FOR_TEAMMATE in the team package already does this step.
====================================================================
#>
$ErrorActionPreference = "Stop"
trap {
    Write-Host ""
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "  Press Enter to close"
    exit 1
}
$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root) -and $MyInvocation.MyCommand.Path) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($Root)) {
    Write-Host "  Run this by double-clicking RUN_2_TEAMMATE_TRUST_CERT.bat - do not paste the text into PowerShell." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}

$cer = @((Join-Path $Root "RevitCadQC_CodeSigning.cer"), (Join-Path $Root "cert\RevitCadQC_CodeSigning.cer")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cer) {
    Write-Host "  RevitCadQC_CodeSigning.cer not found next to this script." -ForegroundColor Red
    Write-Host "  Ask for the team package (RUN_4_MAKE_TEAM_PACKAGE) or run RUN_1_BUILD_AND_INSTALL on the build PC first." -ForegroundColor Red
    Read-Host "  Press Enter to close"; exit 1
}

$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
foreach ($store in @("Root", "TrustedPublisher")) {
    $have = Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object { $_.Thumbprint -eq $c.Thumbprint }
    if ($have) { Write-Host "  already trusted in CurrentUser\$store" -ForegroundColor Green; continue }
    if ($store -eq "Root") { Write-Host "  Windows will ask to trust the certificate - click YES." -ForegroundColor Yellow }
    Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
    Write-Host "  trusted in CurrentUser\$store" -ForegroundColor Green
}
Write-Host ""
Write-Host "  Done: $($c.Subject)  thumbprint $($c.Thumbprint)" -ForegroundColor Green
Read-Host "  Press Enter to close"
