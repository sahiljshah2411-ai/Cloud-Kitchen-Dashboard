<#
  Builds the Revit CAD QC add-in for one or more Revit versions.
  Usage:  .\scripts\build.ps1                 (builds 2024, 2025 and 2026)
          .\scripts\build.ps1 -Versions 2025
  Needs the .NET 8 SDK (https://dotnet.microsoft.com/download). Revit itself is not needed to build.
#>
param([string[]]$Versions = @("2024", "2025", "2026"), [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet test tests/CadQC.Tests -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
    foreach ($v in $Versions) {
        Write-Host "`n=== Building for Revit $v ===" -ForegroundColor Cyan
        dotnet build src/CadQC.Revit/CadQC.Revit.csproj -c $Configuration -p:RevitVersion=$v
        if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $v" }
    }
    dotnet publish src/CadQC.Cli/CadQC.Cli.csproj -c $Configuration -o "$root/dist/cli"
    Write-Host "`nDone. Add-in builds: src/CadQC.Revit/bin/$Configuration/<version>/  Command line: dist/cli/CadQC.exe" -ForegroundColor Green
} finally { Pop-Location }
