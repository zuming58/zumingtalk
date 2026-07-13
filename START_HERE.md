# 祖名闪电说：接手与启动指南

这份文件是另一台电脑或另一位 Codex 的唯一入口。

## 当前完成到哪里

- 已完成产品需求、UI 设计规范、设计令牌、开发交接和测试矩阵。
- 已完成可点击的 React/Vite UI Demo，用来确认视觉和交互。
- 尚未创建正式的 .NET/WPF 工程，也尚未接入麦克风、阿里云 ASR、SQLite、全局热键或自动写入。
- 最终产品必须是 Windows 11 x64 原生应用；`prototype/` 不是正式架构，不能沿着网页端或 Electron 继续开发。

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

## 正式应用的开发起点

在 Windows 11 x64 上安装 .NET 10 SDK，然后从 `docs/DEVELOPMENT_HANDOFF.md` 的 M1 开始：

1. 创建 .NET 10 WPF 解决方案和 App/Application/Domain/Infrastructure 四层工程。
2. 先用固定模拟数据还原首页、设置页、详情抽屉和独立胶囊窗口。
3. 胶囊必须是单独的 `OverlayWindow`：Topmost、NoActivate、空闲时完全隐藏；它不属于主窗口页面。
4. 任何应用处于前台时，短按右 Alt 都能开始或结束听写。即使没有输入框也要识别并保存历史。
5. 只有捕获到可编辑目标时才尝试自动写入；目标不存在或丢失时只保存历史，不覆盖剪贴板。
6. 自动写入不得只依赖模拟 `Ctrl+V`。按交接文档实现 Edit/RichEdit、`WM_PASTE`、`SendInput` 和复制兜底的分层策略，降低 360 等安全软件拦截造成的影响。
7. 实际语音识别使用阿里云实时 ASR，不使用本地 Whisper，不接大模型改写。

## 可以原样发给另一台 Codex 的话

```text
请接手 GitHub 仓库 https://github.com/zuming58/zumingtalk 开发“祖名闪电说”。

先克隆仓库并完整阅读根目录 START_HERE.md，再按它列出的顺序阅读 PRD、设计规范、开发交接和设计 QA。先运行 prototype，确认当前可点击 UI Demo；但不要把网页原型当正式产品继续开发，也不要使用 Electron。

正式产品是 Windows 11 x64 的 .NET 10 WPF 原生应用。请从 docs/DEVELOPMENT_HANDOFF.md 的 M1 开始实施：先建立四层解决方案，用模拟数据像素级还原已确认 UI，并把玻璃胶囊实现为独立 Topmost、NoActivate、空闲隐藏的 OverlayWindow。全局短按右 Alt 在任何页面都应开始/结束听写；没有输入目标时仍保存识别历史。自动写入不能只用 SendInput/Ctrl+V，要严格采用交接文档中的分层兼容策略，并保留复制兜底。语音识别使用阿里云实时 ASR，不使用本地 Whisper，也不做大模型改写。

开始前先检查当前工作树，保留现有设计和文档。每完成一个里程碑都运行相应测试、更新交接记录并提交 Git；不要提交阿里云凭证、Token、真实录音、SQLite 数据库、node_modules 或构建产物。先向我汇报你读到的边界和 M1 实施计划，然后直接开始 M1。
```

## 安全边界

- 不要把 AppKey、AccessKey ID、AccessKey Secret、临时 Token 或真实录音提交到 Git。
- 不使用 DLL 注入、驱动、关闭安全软件、绕过防护或默认管理员常驻。
- 360 兼容目标是采用 Windows 官方能力和可靠兜底，不能承诺所有软件都一定接受自动写入。
