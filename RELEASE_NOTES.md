# Olivia Letter 信箱 v1.0.1

## 修复与增强

- AI 服务类型新增常见服务商预设：OpenAI、Anthropic、Google Gemini、DeepSeek、Moonshot Kimi、智谱 GLM、阿里云百炼 Qwen、火山方舟、xAI Grok、Mistral、Groq、OpenRouter、SiliconFlow、Together AI。
- 各服务商默认地址自动填入，地址仍可修改；自定义 OpenAI 兼容接口和中转站继续可用。
- 模型列表按所选服务地址获取；无上游列表或接口格式不标准时，仍可手动填写模型名。
- API Key 按服务类型分开保存，切换服务商时不会互相覆盖。
- 修复模型名可编辑下拉框出现白底、和暗色界面不统一的问题。
- 强化林离的第一人称身份：来信称呼“林离”时会被理解为称呼回信者，避免把角色当成第三方。
- 保留本地 Ollama 模型管理和常见本地模型预设。
