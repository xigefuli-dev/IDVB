@echo off
setlocal EnableExtensions

rem Start the real overlay_game test window with the pipe consumed by RealCLI.
set "ROOT=%~dp0"
set "PIPE=IDVB.OverlayGame"
set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Release\dwrg.exe"

if not exist "%OVERLAY_EXE%" set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Debug\dwrg.exe"
if not exist "%OVERLAY_EXE%" set "OVERLAY_EXE=%ROOT%Tools\overlay_game\dwrg.exe"

if not exist "%OVERLAY_EXE%" (
    where cmake >nul 2>nul
    if errorlevel 1 (
        echo dwrg.exe was not found and cmake is unavailable.
        echo Install CMake or build Tools\overlay_game manually.
        exit /b 2
    )
    echo dwrg.exe was not found. Building Tools\overlay_game in Release mode...
    cmake -S "%ROOT%Tools\overlay_game" -B "%ROOT%Tools\overlay_game\build" -A x64
    if errorlevel 1 exit /b 2
    cmake --build "%ROOT%Tools\overlay_game\build" --config Release --parallel 2
    if errorlevel 1 exit /b 2
    set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Release\dwrg.exe"
)

if not exist "%OVERLAY_EXE%" (
    echo overlay_game build completed but dwrg.exe was not found.
    exit /b 2
)

for %%I in ("%OVERLAY_EXE%") do set "OVERLAY_DIR=%%~dpI"
echo Starting overlay_game with control pipe: %PIPE%
start "overlay_game" /D "%OVERLAY_DIR%" "%OVERLAY_EXE%" --pipe-name "%PIPE%"
exit /b 0
