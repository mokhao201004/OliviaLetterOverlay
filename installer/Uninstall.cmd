@echo off
rem Uninstall: remove shortcuts and program files (letters data is kept unless -RemoveData)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "Uninstall.ps1" %*
pause
