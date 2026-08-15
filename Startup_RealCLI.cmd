@echo off
setlocal EnableExtensions

rem Attach RealCLI to the already running IDVB GUI and overlay_game processes.
set "ROOT=%~dp0"
set "IDVB_PIPE=IDVB.RealCLI"
set "OVERLAY_PIPE=IDVB.OverlayGame"
set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\IDVB.exe"

if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
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
