# 祖名闪电说：接手与启动指南

这份文件是另一台电脑或另一位 Codex 的唯一入口。

## 当前完成到哪里

- 已完成产品需求、UI 原型和 .NET 10 WPF 四层工程。
- 已接入麦克风、SQLite、全局热键、独立悬浮胶囊、历史记录和分层自动写入。
- v0.6.1 已迁移到阿里云百炼 `fun-asr-realtime`，设置页只需一个加密保存的 API Key，并修复了胶囊定位、Codex 类输入框兼容和设置页布局。
- 2026-07-14 真人测试确认 v0.6.1 仍存在微信/Codex 自动写入和声波反馈问题；继续开发前必须阅读 `docs/HANDOFF-2026-07-14-REAL-TEST-R2.md`，不得把 v0.6.1 当作已验收完成版。
- `prototype/` 只用于视觉回溯；正式产品位于 `src/`。

## 先运行现有 UI Demo

需要 Node.js 20 或更高版本。Windows PowerShell 执行：

```powershell
git clone https://github.com/zuming58/zumingtalk.git
cd zumingtalk\prototype
npm ci
npm run dev
```

打开终端显示的本地地址，通常是 `http://localhost:5173`。在 Demo 中可以切换首页和设置页，操作复制、删除、撤销、更多菜单和详情，并用右 Alt 演示胶囊状态。浏览器原型无法获得系统级全局热键、置顶无激活窗口或向其他应用写入文字的能力，这些要在 WPF 中实现。

若只需检查能否构建：

```powershell
cd zumingtalk\prototype
npm ci
npm run build
```

## 正式开发前的阅读顺序

1. 本文件 `START_HERE.md`
2. `docs/prd/PRD-001.md`
3. `design/DESIGN_SYSTEM.md`
4. `docs/DEVELOPMENT_HANDOFF.md`
5. `design-qa.md`
6. `prototype/README.md`

最终视觉参考在 `design/mockups/home-recording.png`，经过浏览器验证的页面截图在 `design/prototype-captures/`，机器可读颜色与尺寸在 `design/tokens.json`。

## 运行正式应用

开发机安装 .NET 10 SDK 后执行：

```powershell
dotnet build Zumingtalk.sln -c Release
dotnet run --project src\Zumingtalk.App\Zumingtalk.App.csproj -c Release
```

普通用户优先下载自包含 `win-x64` 发布包，解压后直接运行 `Zumingtalk.App.exe`，无需安装 .NET。首次使用在设置页粘贴华北 2（北京）地域的百炼 API Key。

## 当前实现边界

1. 已创建 .NET 10 WPF 解决方案和 App/Application/Domain/Infrastructure 四层工程。
2. 已实现首页、设置页、详情抽屉和独立胶囊窗口。
3. 胶囊是单独的 `OverlayWindow`：Topmost、NoActivate、空闲时完全隐藏；它不属于主窗口页面。
4. 任何应用处于前台时，短按右 Alt 都能开始或结束听写。即使没有输入框也要识别并保存历史。
5. 只有捕获到可编辑目标时才尝试自动写入；目标不存在或丢失时只保存历史，不覆盖剪贴板。
6. 自动写入不得只依赖模拟 `Ctrl+V`。按交接文档实现 Edit/RichEdit、`WM_PASTE`、`SendInput` 和复制兜底的分层策略，降低 360 等安全软件拦截造成的影响。
7. 实际语音识别使用阿里云百炼 `fun-asr-realtime`，不使用本地 Whisper，不接大模型改写。

## 可以原样发给另一台 Codex 的话

```text
请接手 GitHub 仓库 https://github.com/zuming58/zumingtalk 开发“祖名闪电说”。

先克隆仓库并完整阅读根目录 START_HERE.md，再按它列出的顺序阅读 PRD、开发交接、百炼配置与验收报告。prototype 只用于视觉回溯，不要使用 Electron。

正式产品是 Windows 11 x64 的 .NET 10 WPF 原生应用，当前版本 v0.6.1。先运行 build/test 并审计现有实现；语音识别固定使用阿里云百炼 fun-asr-realtime 和一个 DPAPI 加密保存的 API Key。全局短按右 Alt 在任何页面都应开始/结束听写；没有输入目标时仍保存识别历史。自动写入采用分层兼容策略并保留复制兜底。

开始前先检查当前工作树，保留现有设计和文档。每次修改都运行 build/test 并更新验收记录；不要提交百炼 API Key、真实录音、SQLite 数据库、node_modules 或构建产物。
```

## 安全边界

- 不要把百炼 API Key 或真实录音提交到 Git。
- 不使用 DLL 注入、驱动、关闭安全软件、绕过防护或默认管理员常驻。
- 360 兼容目标是采用 Windows 官方能力和可靠兜底，不能承诺所有软件都一定接受自动写入。
