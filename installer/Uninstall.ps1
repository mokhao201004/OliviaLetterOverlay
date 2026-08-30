# OliviaLetterOverlay 卸载脚本
# - 结束程序、移除快捷方式、删除程序目录
# - 信件与设置数据（%LOCALAPPDATA%\OliviaLetterOverlay）默认保留，脚本结尾会告知手动删除路径
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\OliviaLetterOverlay'

Write-Host '== OliviaLetterOverlay 卸载 ==' -ForegroundColor Cyan

Get-Process -Name 'OliviaLetterOverlay' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$wsh = New-Object -ComObject WScript.Shell
$startupLink = Join-Path [Environment]::GetFolderPath('Startup') 'OliviaLetterOverlay 伴随启动.lnk'
$menuLink = Join-Path [Environment]::GetFolderPath('Programs') 'OliviaLetterOverlay.lnk'
foreach ($link in @($startupLink, $menuLink)) {
    if (Test-Path $link) {
        Remove-Item $link -Force
        Write-Host ('已移除快捷方式：' + $link)
    }
}

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
    Write-Host ('已删除程序目录：' + $installDir) -ForegroundColor Green
}

if ($RemoveData) {
    $dataDir = Join-Path $env:LOCALAPPDATA 'OliviaLetterOverlay'
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
        Write-Host '已删除信件与设置数据。' -ForegroundColor Green
    }
}
else {
    Write-Host ''
    Write-Host '信件、记忆与设置数据仍保留在：' (Join-Path $env:LOCALAPPDATA 'OliviaLetterOverlay')
    Write-Host '如需彻底清除（含语音缓存），手动删除该目录，或重跑：'
    Write-Host ('  powershell -NoProfile -ExecutionPolicy Bypass -File ' + $PSScriptRoot + '\Uninstall.ps1 -RemoveData')
}

Write-Host '卸载完成。' -ForegroundColor Green
