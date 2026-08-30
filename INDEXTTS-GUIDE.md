# OliviaLetterOverlay 的 IndexTTS-2.5 部署与版本对比

## 一、先说结论

OliviaLetterOverlay 只把 IndexTTS-2.5 接入为本地朗读引擎，模型权重和 Python 环境仍放在独立的 IndexTTS 目录里。这样主程序发布包保持较小，也不会把模型下载到用户不知情的位置。

主界面只有在检测到完整引擎并且启用了“信件朗读”后，才会显示“朗读”和“重新生成”。

## 二、最简单的部署方式

1. 打开 OliviaLetterOverlay，进入右上角 **AI 模型设置**。
2. 展开 **信件朗读（本地 IndexTTS-2.5）**。
3. 点击 **一键准备**，程序会打开 [IndexTTS 官方仓库](https://github.com/index-tts/index-tts)，并复制安装命令。
4. 按官方说明完成依赖和模型下载。
5. 回到软件点击 **重新检测**。
6. 检测到完整引擎后勾选启用，点击 **保存设置**。

程序会优先自动寻找以下目录：当前设置目录、软件旁边的 `IndexTTS-2.5`、本机默认的 `D:\codex work\IndexTTS-2.5`。

## 三、Windows 手动部署

请预留约 20–25GB 空间，模型检查点和 Python 环境都比较大。

在 PowerShell 中执行官方流程：

```powershell
git clone https://github.com/index-tts/index-tts.git
cd index-tts
pip install -U uv
uv sync --all-extras
uv tool install "huggingface-hub[cli,hf_xet]"
hf download IndexTeam/IndexTTS-2.5 --local-dir=checkpoints
```

如果 Windows 安装 DeepSpeed 失败，可以按官方说明去掉 `--all-extras`，只安装需要的功能；如果 CUDA 报错，请检查 NVIDIA CUDA Toolkit 版本和显卡驱动。

安装完成后，IndexTTS 根目录至少应包含：

```text
.venv\Scripts\python.exe
local_tools\olivia_tts_worker.py
checkpoints\config.yaml
reference\lv_0_reference_6.8-22.1.wav
```

## 四、在软件里怎么使用

1. 在“信件朗读”区域确认引擎状态为“已检测到完整引擎”。
2. 勾选“信件朗读”，保存设置。
3. 选中一封回信，点击“朗读”。
4. 第一次会加载模型并生成 WAV，之后同一封信会使用本地缓存。
5. 对当前音色或节奏不满意时，点击“重新生成”，程序会换一个随机种子。

默认参数是种子 `20260830`、句间停顿 `200ms`、语速倍率 `1.0`、每段最多 `120` tokens。语速倍率越大，生成音频持续时间越长。

## 五、显存和失败处理

显卡模式使用分阶段加载，尽量不让全部模块同时驻留显存；显存不足时会提示切换 CPU 慢速生成。CPU 模式会明显更慢，但不需要显存。

游戏运行时可能占用显存，遇到生成失败可以先关闭游戏，或在提示框中选择 CPU 模式。

## 六、为什么没有按钮

以下任一项缺失，按钮都会隐藏：

- 没有勾选启用信件朗读。
- `.venv\Scripts\python.exe` 不存在。
- `local_tools\olivia_tts_worker.py` 不存在。
- `checkpoints\config.yaml` 不存在。
- 默认参考音频或你指定的参考音频不存在。

修复路径后点击“重新检测”，不需要重装软件。

## 七、相对上个版本新增了什么

以下按项目上一版 `1.0.1` 的发布记录与当前 `1.1` 实现对比：

| 模块 | 1.0.1 | 1.1 |
| --- | --- | --- |
| 本地语音 | 没有本地 IndexTTS 接入 | IndexTTS-2.5 本地朗读、整封回信播放 |
| 语音操作 | 无 | 朗读、停止、取消、重新生成 |
| 语音缓存 | 无 | 按角色和信件缓存 WAV，重启后可复用 |
| 显存策略 | 无 | GPU 分阶段推理，显存不足可切 CPU |
| 语音配置 | 无 | 引擎目录、参考音色、种子、停顿、语速、切分长度 |
| 部署体验 | 手动填写路径 | 一键打开官方部署页、复制命令、自动检测和自动关联 |
| 按钮显示 | 无条件显示朗读入口 | 引擎不可用时隐藏朗读和重新生成 |
| 诊断 | 通用日志 | 增加 TTS 阶段、缓存、退出码和失败原因记录 |
| 角色数据 | 单一默认信箱 | 角色切换、信件和记忆按角色隔离 |
| 信件预览 | 普通分页图片 | 固定纸张和边框，文字独立滚动，支持居中全屏预览 |
| 历史记录 | 固定标题 | 每条历史聊天可单独重命名 |

## 八、当前边界

- 1.1 发布包不包含 IndexTTS 模型和 Python 虚拟环境，避免把安装包膨胀到二十多 GB。
- 语音文本只交给本机 worker 处理，不上传云端；AI 回信本身仍取决于你选择的云端或本地模型。
- 当前 Olivia 集成默认使用参考音频继承音色和情绪，没有把 IndexTTS-2.5 的全部情绪控制参数暴露到界面。
- IndexTTS 本身的模型许可、依赖许可和商业使用要求，以[官方仓库](https://github.com/index-tts/index-tts)及其随附许可证为准。

