@echo off
rem ============================================================
rem  Desktop Pet / Phoebe - launcher
rem
rem  Run build.cmd first if PhoebePet.dll does not exist yet.
rem  This script also auto-builds it once if missing.
rem
rem  NOTE: keep this file ASCII-only and avoid parentheses in
rem  rem comments - cmd.exe mis-parses them inside if blocks.
rem ============================================================
setlocal

set "DOTNET_ROOT=C:\Program Files\dotnet"
set "DOTNET_CLI_HOME=%~dp0.dotnet"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

if not exist "%DOTNET_ROOT%\dotnet.exe" goto nodotnet
cd /d "%~dp0"
if not exist "PhoebePet.dll" goto autobuild

rem Launch through PowerShell with a hidden window.
rem
rem Why not just "start "" dotnet.exe PhoebePet.dll":
rem   dotnet.exe is a CONSOLE-subsystem program, so Windows gives it a console
rem   window no matter how it is started - a black cmd box flashes and stays
rem   behind the pet. The pet itself is pure WinForms and needs no console.
rem   -WindowStyle Hidden starts the child with STARTF_USESHOWWINDOW/SW_HIDE,
rem   so the console is created but never shown.
rem
rem Note: this still costs one short-lived cmd.exe, because .cmd files run
rem inside one. It exits immediately, so nothing stays on screen.
if exist "%~dp0start-pet.vbs" goto usevbs
powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%DOTNET_ROOT%\dotnet.exe' -ArgumentList '%CD%\PhoebePet.dll' -WorkingDirectory '%CD%' -WindowStyle Hidden"
exit /b 0

:usevbs
rem Preferred path when the VBS shim is present: wscript has no console at all.
wscript.exe //nologo "%~dp0start-pet.vbs"
exit /b 0

:nodotnet
echo [ERROR] dotnet not found at "%DOTNET_ROOT%\dotnet.exe"
echo         Please install the .NET 8 or newer Desktop Runtime.
pause
exit /b 1

:autobuild
echo [INFO] Not built yet - compiling now...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 goto buildfail
if exist "%~dp0start-pet.vbs" goto usevbs
powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%DOTNET_ROOT%\dotnet.exe' -ArgumentList '%CD%\PhoebePet.dll' -WorkingDirectory '%CD%' -WindowStyle Hidden"
exit /b 0

:buildfail
echo [ERROR] Build failed.
pause
exit /b 1
