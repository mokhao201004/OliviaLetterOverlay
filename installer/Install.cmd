@echo off
rem One-click install (per-user, no admin needed)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "Install.ps1" -Launch
pause
