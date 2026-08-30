# 还原游戏原启动器：删除包装器 launcher.exe，把 launcher.origin.exe 改名回去
param(
    [string]$GameRoot = 'D:\Program Files (x86)\Steam\steamapps\common\BSide Olivia Lin Test'
)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $GameRoot 'launcher.exe'
$origin = Join-Path $GameRoot 'launcher.origin.exe'
$marker = Join-Path $GameRoot 'olivia-mail-wrapper.marker'

if (-not (Test-Path $origin)) {
    Write-Host '未检测到包装安装（launcher.origin.exe 不存在），无需还原。' -ForegroundColor Yellow
    return
}

$gameProc = Get-Process -Name 'Olivia', 'launcher' -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($GameRoot, [System.StringComparison]::OrdinalIgnoreCase)
}
if ($gameProc) {
    throw '游戏或其启动器正在运行，请先完全退出游戏再还原。'
}

if (Test-Path $launcher) {
    Remove-Item $launcher -Force
}

Rename-Item $origin 'launcher.exe'
if (Test-Path $marker) {
    Remove-Item $marker -Force
}

Write-Host '已还原游戏原启动器。' -ForegroundColor Green
