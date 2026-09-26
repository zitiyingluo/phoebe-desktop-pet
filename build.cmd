@echo off
rem ============================================================
rem  Desktop Pet / Phoebe - build script
rem
rem  Produces PhoebePet.dll + PhoebePet.runtimeconfig.json
rem  Re-run this after editing pet.cs.
rem
rem  NOTE: keep this file ASCII-only and avoid parentheses in
rem  rem comments - cmd.exe mis-parses them inside if blocks.
rem ============================================================
setlocal
set "DOTNET_ROOT=C:\Program Files\dotnet"
set "DOTNET_CLI_HOME=%~dp0.dotnet"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto buildfail

echo.
echo Build finished. Double-click start-pet.cmd to launch the pet.
pause
exit /b 0

:buildfail
echo.
echo [ERROR] Build failed.
pause
exit /b 1
