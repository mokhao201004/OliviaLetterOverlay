# OliviaLetterOverlay 一键安装脚本
# - 优先用 dotnet 发布自包含单文件；没有 SDK 时回退到已有的构建产物
# - 安装到每用户目录（不需要管理员）： %LOCALAPPDATA%\Programs\OliviaLetterOverlay
# - 创建两个快捷方式：开始菜单入口 + 开机伴随启动（--watch，检测到游戏自动弹出信箱）
# - 重复运行即为升级覆盖：自动结束正在运行的实例后原地覆盖，信件与设置不受影响
param(
    [switch]$Launch,
    [string]$GameRoot = 'D:\Program Files (x86)\Steam\steamapps\common\BSide Olivia Lin Test'
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot
$project = Join-Path $repoRoot 'OliviaLetterOverlay.csproj'
$publishDir = Join-Path $repoRoot 'bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
$looseBuild = Join-Path $repoRoot 'bin\Release\net10.0-windows10.0.19041.0'
$bundledPayload = Join-Path $scriptRoot 'payload'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\OliviaLetterOverlay'
$exeName = 'OliviaLetterOverlay.exe'

Write-Host '== OliviaLetterOverlay 安装 ==' -ForegroundColor Cyan

# 1) 结束正在运行的实例（覆盖文件需要）
$running = Get-Process -Name 'OliviaLetterOverlay' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '正在结束运行中的信箱程序...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# 2) 取得最新发布文件：发布压缩包优先使用 installer\payload，源码目录才重新 publish。
if (Test-Path (Join-Path $bundledPayload $exeName)) {
    Write-Host '使用安装包内置的自包含发布文件。'
    $source = $bundledPayload
}
else {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet -and (Test-Path $project)) {
    Write-Host '使用 dotnet 发布自包含版本（首次约 1-2 分钟）...'
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败，请把上面的报错发给开发者。' }
    $source = $publishDir
    }
    elseif (Test-Path (Join-Path $publishDir $exeName)) {
    Write-Host 'dotnet 不可用，使用已有的发布产物。'
    $source = $publishDir
    }
    elseif (Test-Path (Join-Path $looseBuild $exeName)) {
    Write-Host 'dotnet 不可用，使用普通构建产物（需要本机已装 .NET 10 桌面运行时）。'
    $source = $looseBuild
    }
    else {
    throw '没有找到可用的构建产物。请先安装 .NET 10 SDK 后重试，或把 publish 输出手动拷到安装目录。'
    }
}

# 3) 覆盖安装
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $installDir -Force -Recurse
Write-Host ('已安装到：' + $installDir) -ForegroundColor Green

# 4) 快捷方式：开机伴随启动 + 开始菜单入口
$wsh = New-Object -ComObject WScript.Shell
$startupDir = [Environment]::GetFolderPath('Startup')
$menuDir = [Environment]::GetFolderPath('Programs')
$startupLink = $wsh.CreateShortcut((Join-Path $startupDir 'OliviaLetterOverlay 伴随启动.lnk'))
$startupLink.TargetPath = Join-Path $installDir $exeName
$startupLink.Arguments = '--watch'
$startupLink.WorkingDirectory = $installDir
$startupLink.Description = '开机静默运行，检测到 Olivia 游戏窗口后自动打开信箱'
$startupLink.Save()
$menuLink = $wsh.CreateShortcut((Join-Path $menuDir 'OliviaLetterOverlay.lnk'))
$menuLink.TargetPath = Join-Path $installDir $exeName
$menuLink.WorkingDirectory = $installDir
$menuLink.Save()
Write-Host '已创建：开机伴随启动快捷方式 + 开始菜单入口' -ForegroundColor Green

# 5) 启动器包装：正常启动游戏时自动拉起信箱（不需要证书，不改游戏文件内容，可随时还原）
$wrapperScript = Join-Path $PSScriptRoot 'install-launcher-wrapper.ps1'
if (Test-Path (Join-Path $GameRoot 'launcher.exe')) {
    try {
        & $wrapperScript -GameRoot $GameRoot
    }
    catch {
        Write-Host ('启动器包装安装失败（不影响其他功能）：' + $_.Message) -ForegroundColor Yellow
    }
}
else {
    Write-Host ('未在 ' + $GameRoot + ' 找到 launcher.exe，跳过启动器包装；信箱仍可通过开机伴随启动使用。') -ForegroundColor Yellow
}

# 6) 语音引擎提示（可选功能，不影响安装）
$engineDir = 'D:\codex work\IndexTTS-2.5'
if (Test-Path (Join-Path $engineDir '.venv\Scripts\python.exe')) {
    Write-Host ('语音引擎已就绪：' + $engineDir) -ForegroundColor Green
}
else {
    Write-Host '提示：未检测到语音引擎目录，信件朗读功能需要在设置里指定引擎路径。' -ForegroundColor Yellow
}

# 6) 立即启动（伴随模式：静默等待游戏窗口）
if ($Launch -or $host.Name -eq 'ConsoleHost') {
    Start-Process -FilePath (Join-Path $installDir $exeName) -WorkingDirectory $installDir -ArgumentList '--watch'
    Write-Host '已启动信箱程序（伴随模式：检测到游戏窗口后自动弹出）。' -ForegroundColor Green
}

Write-Host ''
Write-Host '安装完成。要点：'
Write-Host ('  程序位置：' + $installDir)
Write-Host '  开机伴随启动：已启用（重启系统后自动静默等待游戏）'
Write-Host '  信件与设置数据：' + (Join-Path $env:LOCALAPPDATA 'OliviaLetterOverlay') + '（卸载不会删除）'
Write-Host '  卸载：运行安装目录里的 Uninstall.cmd'
