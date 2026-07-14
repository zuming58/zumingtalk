# 祖名闪电说

Windows 11 云端语音听写工具。短按右 Alt 开始/结束录音，使用用户自己的阿里云百炼 Fun-ASR 实时转录，并把最终文本写入当前光标位置。

当前仓库已包含可运行的 .NET 10 WPF 应用。换一台电脑或交给另一位 Codex 时，请先阅读 [START_HERE.md](START_HERE.md)。

## 开发前必读

1. [接手与启动指南](START_HERE.md)
2. [产品需求文档](docs/prd/PRD-001.md)
3. [UI 设计规范](design/DESIGN_SYSTEM.md)
4. [开发交接文档](docs/DEVELOPMENT_HANDOFF.md)
5. [最终首页视觉稿](design/mockups/home-recording.png)
6. [可点击 UI Demo](prototype/README.md)
7. [设计 QA](design-qa.md)
8. [百炼 API Key 配置与测试](docs/BAILIAN_SETUP.md)

## V1 技术方向

- Windows 11 x64
- .NET 10 LTS + WPF
- 阿里云百炼 `fun-asr-realtime`
- SQLite 本地历史
- 无网页端、无本地 Whisper、无大模型改写

`prototype/` 是交互和视觉交接用的浏览器原型，不是最终产品架构。最终应用仍按 .NET 10 WPF 实现。

## 安全提醒

不得把百炼 API Key 或真实用户录音提交到 Git。API Key 只允许通过本机设置页输入，并使用 Windows 当前用户级 DPAPI 加密保存。
