@echo off
REM ====================================================================
REM  BUILD ONLY - R2022 - Revit can stay OPEN
REM ====================================================================
REM  RUN_1_BUILD_AND_INSTALL blocks while Revit runs, and it is RIGHT to.
REM  It copies the DLL into
REM      %APPDATA%\Autodesk\Revit\Addins\2022\RevitCadQC\
REM  and Revit LOCKS that copy at startup. The copy cannot be replaced
REM  while Revit is open.
REM
REM  But bin\R2022\ is NOT locked. Nothing loads from there.
REM  So this builds and stops - no install, no Revit check, no signing.
REM
REM  Loop:
REM      edit  ->  RUN_BUILD_ONLY_R2022  ->  Add-in Manager (Faceless)
REM
REM  Load bin\R2022\RevitCadQC.dll in Add-in Manager once
REM  (command RevitCadQC.Commands.RunQcCommand), then Faceless re-runs
REM  it after every build. No restart.
REM
REM  Run RUN_1_BUILD_AND_INSTALL as normal when you are ready to deploy.
REM ====================================================================

setlocal
cd /d "%~dp0"

set DOTNET=
for %%D in (
  "%USERPROFILE%\.dotnet\dotnet.exe"
  "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
  "%USERPROFILE%\dotnet\dotnet.exe"
  "%ProgramFiles%\dotnet\dotnet.exe"
) do if not defined DOTNET if exist %%D set DOTNET=%%D

if not defined DOTNET (
  for /f "delims=" %%P in ('where dotnet 2^>nul') do if not defined DOTNET set DOTNET="%%P"
)

if not defined DOTNET (
  echo.
  echo   dotnet SDK not found. Run RUN_1_BUILD_AND_INSTALL once -
  echo   it installs a user-local SDK with no admin rights.
  echo.
  pause
  exit /b 1
)

echo.
echo   Building R2022 only. Revit may stay open.
echo.

%DOTNET% build RevitCadQC.csproj -c R2022
if errorlevel 1 (
  echo.
  echo   BUILD FAILED - fix the errors above.
  echo.
  pause
  exit /b 1
)

echo.
echo   BUILD OK
echo   DLL: %CD%\bin\R2022\RevitCadQC.dll
echo.
echo   Now: Add-in Manager (Manual Mode, Faceless) to re-run.
echo.
pause
