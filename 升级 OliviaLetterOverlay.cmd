@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo == Olivia Letter 信箱升级程序 ==
echo 请先关闭旧版信箱，升级完成前不要关闭此窗口。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Install.ps1" -Launch
if errorlevel 1 (
    echo.
    echo 升级失败，请把上面的错误信息发给开发者。
    pause
    exit /b 1
)
echo.
echo 升级完成，旧信件、记忆、人设和设置已保留。
pause
