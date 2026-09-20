@echo off
setlocal EnableExtensions

rem Request administrator privileges if not already elevated.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

rem Start the normal IDVB GUI and expose its real SessionOrchestrator to RealCLI.
set "ROOT=%~dp0"
cd /d "%ROOT%"
set "PIPE=IDVB.RealCLI"

rem Prefer the x64 outputs used by IDVBuff.slnx.
set "IDVB_EXE=%ROOT%bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

rem If x64 Debug does not exist, fall back to other candidates
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
if not exist "%IDVB_EXE%" set "IDVB_EXE=%ROOT%bin\Release\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"

rem If bin\Debug exists and is newer than the chosen binary, prefer bin\Debug
if exist "%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe" (
    xcopy /d /l /y "%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe" "%IDVB_EXE%" 2>nul | findstr /b /c:"1 " >nul
    if not errorlevel 1 (
        set "IDVB_EXE=%ROOT%bin\Debug\net10.0-windows10.0.19041.0\win-x64\IDVB.exe"
    )
)

if not exist "%IDVB_EXE%" (
    echo [错误] 未找到 IDVB.exe。
    echo 请先运行 dotnet build 构建项目后再启动。
    pause
    exit /b 2
)

echo ========================================================
echo  Identity Vision Bridge (IDVB) 启动器
echo ========================================================
echo  程序路径: %IDVB_EXE%
echo  控制管道: %PIPE%
echo ========================================================

tasklist /fi "imagename eq IDVB.exe" 2>nul | find /i "IDVB.exe" >nul
if %errorlevel% equ 0 (
    echo [警告] 检测到系统中已有正在运行的 IDVB.exe 进程！
    echo 若旧进程未关闭，可能会被重定向唤醒旧版本窗口。
)

echo 正在启动 IDVB GUI...
start "Identity Vision Bridge" /D "%ROOT%" "%IDVB_EXE%" --isolated-dev-instance --idvb-control-pipe "%PIPE%"
exit /b 0