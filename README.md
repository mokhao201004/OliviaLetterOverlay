# Olivia Letter 信箱

一个面向 Windows 的本地 AI 信箱应用。它提供书信式对话体验，支持 MiMo、OpenAI 兼容接口和本地 Ollama；信件、记忆与人设数据默认保存在本机。

> 本项目使用来自 [1Dreamer666/olivia-lin](https://github.com/1Dreamer666/olivia-lin) 的人设素材；该项目采用 AGPL-3.0 许可，因此本项目也按 AGPL-3.0 发布，详见 `THIRD_PARTY_NOTICES.md`。

## 功能

- 书信式聊天：来信与回信渲染为可下载/分享的信纸图片。
- 多模型接入：MiMo、OpenAI 兼容接口、本地 Ollama。
- 本地模型管理：可下载和选择适合设备的 Ollama 模型。
- 人设分析：上传参考聊天图片，提取人设、记忆和参考信件。
- 记忆与主动来信：支持长期记忆，主动来信间隔最低 10 分钟。
- 游戏伴随启动：开机后台静默等待，检测到 Olivia 游戏主窗口后自动打开信箱。
- 个人照片邮票：发信信纸保留本地人像邮票装饰。

## 下载与运行

1. 从 Releases 下载 `OliviaLetterOverlay-1.0.0-win-x64.zip`。
2. 解压到任意目录。
3. 双击 `OliviaLetterOverlay.exe`。

推荐 64 位 Windows 10/11。发布包为 self-contained，通常无需额外安装 .NET 运行时。

## AI 配置

点击右上角设置按钮：

- **MiMo**：填写 API Key，模型固定为 `mimo-v2.5`。
- **OpenAI 兼容接口**：填写 Base URL、模型名和 API Key。
- **Ollama**：填写本地服务地址和模型名，默认 `http://127.0.0.1:11434`。

API Key 保存在当前 Windows 用户本地配置中，源码和发布包不包含密钥。

## 游戏伴随启动

程序支持：

```text
OliviaLetterOverlay.exe --watch
```

发布包已附带 `Olivia Letter 游戏伴随启动` 快捷方式思路：将带 `--watch` 参数的快捷方式放入 Windows 启动文件夹后，程序开机静默运行；检测到 `Olivia` 游戏主窗口时自动显示信箱。

## 数据位置

用户数据保存在：

```text
%LOCALAPPDATA%\OliviaLetterOverlay
```

包括信件、人设、记忆和 AI 提供商设置。卸载程序不会自动删除它们；需要彻底清除时请手动备份或删除该目录。

## 从源码构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build .\OliviaLetterOverlay.csproj -c Release
dotnet publish .\OliviaLetterOverlay.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## 许可

本项目基于 AGPL-3.0 许可发布，详见 `LICENSE`。这是独立本地工具，不是官方客户端，也不代表原作品作者或发行方。

