@echo off
setlocal EnableExtensions

rem Request administrator privileges if not already elevated.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

rem Start the normal IDVB GUI and expose its real SessionOrchestrator to RealCLI.
set "ROOT=%~dp0"
set "PIPE=IDVB.RealCLI"
rem Prefer the x64 outputs used by IDVBuff.slnx. Older AnyCPU outputs may
rem still exist and can contain a previous product version.
set "IDVB_EXE=%ROOT%bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

if not exist "%IDVB_EXE%" (
    echo IDVB.exe was not found.
    echo Build IDVBuff.csproj first, then run this command again.
    pause
    exit /b 2
)

echo Starting IDVB GUI with control pipe: %PIPE%
rem Do not hand this development launch to an already-running installed copy.
start "Identity Vision Bridge" "%IDVB_EXE%" --isolated-dev-instance --idvb-control-pipe "%PIPE%"
exit /b 0
