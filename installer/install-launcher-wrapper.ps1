# 安装启动器包装（模仿 linli-local-mail 的 launcher-wrapper 逻辑，但不需要证书、不改游戏文件内容）：
# 1. 游戏根目录的 launcher.exe 改名为 launcher.origin.exe（保留原样备份）
# 2. 用系统自带 csc 把本目录的 Program.cs 编译成同名 launcher.exe 包装器
# 3. 玩家从 Steam/桌面正常启动游戏时：包装器先拉起信箱（伴随模式），再原样启动原启动器
# 还原：运行 restore-launcher-wrapper.ps1
param(
    [string]$GameRoot = 'D:\Program Files (x86)\Steam\steamapps\common\BSide Olivia Lin Test'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$source = Join-Path $here 'LauncherWrapper\Program.cs'
$launcher = Join-Path $GameRoot 'launcher.exe'
$origin = Join-Path $GameRoot 'launcher.origin.exe'
$wrapper = Join-Path $GameRoot 'launcher.exe'
$marker = Join-Path $GameRoot 'olivia-mail-wrapper.marker'

Write-Host '== 安装启动器包装 ==' -ForegroundColor Cyan

if (-not (Test-Path $launcher)) {
    throw ('未找到 ' + $launcher + '，请用 -GameRoot 指定游戏根目录。')
}

if (Test-Path $origin) {
    Write-Host '检测到已安装过包装（launcher.origin.exe 已存在），跳过改名。' -ForegroundColor Yellow
}
else {
    # 游戏本体运行时 launcher.exe 可能被占用，先提示关闭。
    # 只认游戏目录下的进程：系统里可能有别的软件恰好叫 launcher。
    $gameProc = Get-Process -Name 'Olivia', 'launcher' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($GameRoot, [System.StringComparison]::OrdinalIgnoreCase)
    }
    if ($gameProc) {
        throw '游戏或其启动器正在运行，请先完全退出游戏再安装包装。'
    }

    Rename-Item $launcher 'launcher.origin.exe'
    Write-Host '已备份原启动器为 launcher.origin.exe' -ForegroundColor Green
}

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    if (Test-Path $origin) {
        Rename-Item $origin 'launcher.exe'
    }
    throw '未找到系统自带的 csc.exe（.NET Framework 编译器）。'
}

& $csc /nologo /target:winexe /out:$wrapper /r:System.dll $source
if ($LASTEXITCODE -ne 0) {
    if ((Test-Path $wrapper) -and -not (Test-Path $origin)) {
        Remove-Item $wrapper -Force
        Rename-Item $origin 'launcher.exe'
    }
    throw '包装器编译失败。'
}

Set-Content -Path $marker -Value (Get-FileHash (Join-Path $GameRoot 'launcher.origin.exe')).Hash
Write-Host '启动器包装安装完成。' -ForegroundColor Green
Write-Host '效果：正常启动游戏时，信箱会自动以伴随模式拉起。'
Write-Host '还原：运行 restore-launcher-wrapper.ps1 即可恢复原启动器。'
