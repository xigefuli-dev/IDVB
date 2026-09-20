@echo off
setlocal EnableExtensions

rem Attach RealCLI to the already running IDVB GUI and overlay_game processes.
set "ROOT=%~dp0"
set "IDVB_PIPE=IDVB.RealCLI"
set "OVERLAY_PIPE=IDVB.OverlayGame"
rem Prefer the x64 outputs used by IDVBuff.slnx. Older AnyCPU outputs may
rem still exist and can contain a previous product version.
set "IDVB_EXE=%ROOT%bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

if not exist "%IDVB_EXE%" (
    echo IDVB.exe was not found.
    exit /b 2
)

echo Attaching RealCLI to IDVB pipe: %IDVB_PIPE%
echo Attaching RealCLI to overlay_game pipe: %OVERLAY_PIPE%
echo RealCLI quit will not close either external process.
"%IDVB_EXE%" --cli --idvb-pipe "%IDVB_PIPE%" --overlay-game-pipe "%OVERLAY_PIPE%" --game-map-xbutton1
exit /b %ERRORLEVEL%
