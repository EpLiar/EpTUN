@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "VPN_EXE=%~dp0bin\Release\net8.0-windows\EpTUN.exe"
set "VPN_CFG=%~dp0appsettings.json"
set "TUN_EXE=%~dp0bin\Release\net8.0-windows\tun2socks.exe"
set "TUN_DLL=%~dp0bin\Release\net8.0-windows\wintun.dll"

:: Auto-elevate to Administrator
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting administrator permission...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

if not exist "%VPN_EXE%" (
    echo [ERROR] Program not found: %VPN_EXE%
    echo Build Release output first.
    pause
    exit /b 1
)

if not exist "%VPN_CFG%" (
    echo [ERROR] Config not found: %VPN_CFG%
    pause
    exit /b 1
)

if not exist "%TUN_EXE%" (
    echo [ERROR] tun2socks not found: %TUN_EXE%
    pause
    exit /b 1
)

if not exist "%TUN_DLL%" (
    echo [ERROR] wintun.dll not found: %TUN_DLL%
    pause
    exit /b 1
)

echo.
echo ==========================================
echo   EpTUN One-Click Launcher
echo ==========================================
echo Config: %VPN_CFG%
echo.
echo Press Ctrl+C in this window to stop VPN.
echo.

"%VPN_EXE%" --config "%VPN_CFG%"

echo.
echo EpTUN exited.
pause

