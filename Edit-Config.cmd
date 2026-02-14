@echo off
setlocal
cd /d "%~dp0"
start "" notepad "%~dp0appsettings.json"
