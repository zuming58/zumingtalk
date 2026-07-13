# 祖名闪电说 V1 开发交接文档

## 1. 交接目标

本文件供另一台电脑上的 Codex 或开发者直接开工。实现前必须同时阅读：

1. [产品需求文档](prd/PRD-001.md)
2. [UI 设计规范](../design/DESIGN_SYSTEM.md)
3. [最终首页视觉稿](../design/mockups/home-recording.png)
4. [机器可读设计令牌](../design/tokens.json)
5. [可点击 UI Demo](../prototype/README.md)
6. [设计 QA](../design-qa.md)

当前仓库不包含 Windows 功能代码。`prototype/` 是可点击的 UI Demo，可用于确认页面和交互，但不得把它作为最终网页架构继续开发。

## 2. 明确技术决策

- 平台：Windows 11 x64 原生桌面应用。
- 框架：.NET 10 LTS + WPF。
- 发布：`win-x64` 自包含版本。
- 识别：阿里云实时语音识别，火山引擎只预留接口。
- 音频：16 kHz、16 bit、单声道 PCM，同时落本地 WAV。
- 数据：SQLite + 本地录音目录。
- 全局听写：右 Alt 短按切换开始/结束，Esc 取消。
- 备用热键：固定 `Ctrl + Win + Space`，只在右 Alt 被安全软件拦截时启用；V1 不允许任意自定义。
- 网页端、Electron、本地 Whisper 和大模型改写均不在 V1 范围。

## 3. 推荐工程结构

```text
src/
  Zumingtalk.App/              WPF 窗口、页面、托盘、悬浮层
  Zumingtalk.Application/      听写协调、历史、统计、保留策略
  Zumingtalk.Domain/           记录、状态、设置和接口
  Zumingtalk.Infrastructure/   阿里云、SQLite、录音、Windows API
tests/
  Zumingtalk.UnitTests/
  Zumingtalk.IntegrationTests/
docs/
design/
```

UI 不直接调用阿里云、SQLite 或 Win32。所有流程由 `DictationCoordinator` 编排。

### 3.1 主窗口与悬浮层是两个独立窗口

- `MainWindow`：首页、设置和历史；关闭时进入系统托盘，不影响全局听写。
- `OverlayWindow`：只负责胶囊状态，必须是独立 Topmost 窗口，不能作为 `MainWindow` 的子元素。
- `OverlayWindow` 使用无边框、透明背景、`ShowInTaskbar=false`、`ShowActivated=false`，并添加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT` 扩展样式。
- 第一次右 Alt：无论当前是否存在输入框都开始录音；根据当前前台窗口调用 `MonitorFromWindow` / `GetMonitorInfo`，把胶囊放到该显示器工作区底部居中、距任务栏 12 px。
- 第二次右 Alt：胶囊切换为“识别中”；完成或失败提示结束后隐藏窗口，不恢复主窗口。
- Esc：立即取消并隐藏胶囊。
- 空闲时不显示透明占位层，避免遮挡点击或触发安全软件误判。

## 4. 自动写入兼容性设计

### 4.1 问题定义

用户已确认：开启 360 时，原“闪电说”有时无法把结果写入光标位置；关闭 360 后恢复。可能被阻止的是低级键盘钩子、模拟按键、跨进程消息或其中的组合。

不得通过关闭安全软件、注入 DLL、安装驱动、绕过防护或默认提权来解决。目标是使用 Windows 官方能力建立多级兼容路径，并在失败时可靠保留文字。

### 4.2 写入前目标捕获

开始录音时记录前台顶层窗口句柄、线程 ID、进程 ID、进程名、`GetGUIThreadInfo` 返回的焦点控件和当前完整性级别。录音胶囊必须使用不激活窗口样式，不能改变上述目标。

目标捕获结果必须分类：

- **Editable**：检测到可编辑控件和有效插入点；识别完成后尝试自动写入，同时保存历史。
- **None**：当前没有可编辑控件或插入点；仍然录音识别，只保存历史，胶囊显示“已保存”。
- **Lost**：录音期间目标窗口关闭、控件销毁或焦点上下文失效；按 None 处理，禁止猜测新目标并插入。

`None` 和 `Lost` 都不是错误，不触发剪贴板事务，不显示“写入失败”。

### 4.3 分层写入策略

按目标控件能力选择一种路径，不允许在“结果未知”时连续尝试多种方法，避免重复插入。

1. **原生 Edit/RichEdit 首选：`EM_REPLACESEL`**
   - 仅对已确认的 Edit/RichEdit 控件使用。
   - 无选区时在插入点写入，有选区时替换选区，并允许撤销。
   - 不依赖模拟 `Ctrl+V`。
2. **标准剪贴板消息：`WM_PASTE`**
   - 对明确支持粘贴消息的原生控件使用。
   - 写入前暂存旧剪贴板；确认成功后恢复。
3. **输入模拟回退：`SendInput(Ctrl+V)`**
   - 用于 Chromium、Electron、浏览器和其他自绘编辑器。
   - 仅在目标与本程序完整性级别兼容时使用。
   - 检查返回的已发送事件数，但不能把返回成功等同于目标一定接收成功。
4. **用户复制兜底**
   - 明确失败或无法验证时，把最终文本保留在剪贴板。
   - 悬浮提示“未能自动写入 · 文字已复制”。
   - 历史卡片始终保留复制入口。

只有捕获结果为 Editable 且所选写入策略明确失败时，才进入第 4 路径。None/Lost 直接保存历史，不覆盖用户剪贴板。

微软官方说明 `SendInput` 受 UIPI 限制，只能向相同或更低完整性级别的应用注入输入，且失败返回值不会明确指出 UIPI 是原因；因此不能承诺对管理员权限目标自动写入。[SendInput 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

原生 Edit/RichEdit 可使用 `EM_REPLACESEL` 在插入点写入或替换当前选区，这一路径不需要模拟键盘组合键。[EM_REPLACESEL 文档](https://learn.microsoft.com/en-us/windows/desktop/Controls/em-replacesel)

### 4.4 剪贴板事务

1. 读取并暂存旧剪贴板，最多重试三次处理“剪贴板正被占用”。
2. 写入最终文本。
3. 执行选定写入策略。
4. 只有在可验证成功时才恢复旧剪贴板。
5. 失败或结果不可验证时保留最终文本，并显示非激活提示。

### 4.5 兼容性设置页

必须显示主热键状态、固定备用热键开关、最近目标应用、最近写入策略、“测试自动写入”按钮，以及“自动选择 / 仅复制”模式。不得提供“关闭 360”“关闭 UAC”或“以管理员身份常驻”的快捷操作。

### 4.6 降低安全软件误报

- 不使用 DLL 注入、驱动、进程内补丁或键盘记录持久化。
- 低级键盘钩子只识别右 Alt 和录音中的 Esc，不记录其他按键内容。
- 日志不得记录用户按键序列、完整转录或剪贴板旧内容。
- 发布构建固定版本信息、公司/产品名和 SHA-256 校验值。
- 正式分发建议使用可信 Authenticode 代码签名；签名能降低误报概率，但不能保证所有安全软件放行。
- 如果 360 仍拦截，只指导用户将明确的发布目录加入信任区，不指导关闭整体防护。

## 5. UI 实现要求

- WPF 资源必须由 `design/tokens.json` 转换，不在页面内散落色值。
- 录音胶囊单独窗口，使用 `ShowActivated=false`、不进入任务栏并保持不抢焦点。
- 页面首先实现 1440×1024 视觉稿，再验证 100%、125%、150% 缩放。
- 所有图标按钮至少 36×36 px 命中区域，并设置 `AutomationProperties.Name`。
- 安全软件受阻提示必须是非模态、非激活，不切换用户当前窗口。

## 6. 开发顺序

### M1：静态壳与视觉验收

- 建立解决方案和四层项目。
- 完成首页、设置页、详情抽屉、更多菜单、玻璃胶囊与错误提示。
- 使用 `prototype/` 的固定模拟数据和交互，像素级对齐视觉稿与已通过的 `design-qa.md`。

### M2：本地能力

- SQLite、录音目录、历史卡片、三天清理和累计统计。
- 麦克风录音、音量平滑、WAV 落盘和播放。
- 系统托盘、窗口关闭到托盘。

### M3：听写流程

- 右 Alt 钩子、Esc 取消、10 分钟上限和备用热键。
- 阿里云 Token、WebSocket 实时识别、顺滑、标点和重试。

### M4：写入与兼容

- 目标捕获、控件识别和三层写入策略。
- 360 开启/关闭矩阵测试。
- 写入失败提示、复制兜底和兼容性设置。

### M5：发布

- 单元测试、集成测试、人工验收。
- 自包含 `win-x64` 发布、版本信息、SHA-256 和使用说明。

## 7. 必测矩阵

| 目标 | 普通权限 | 管理员权限 | 360 关闭 | 360 开启 |
| --- | --- | --- | --- | --- |
| Windows 记事本 | 必测 | 必测 | 必测 | 必测 |
| VS Code | 必测 | 必测 | 必测 | 必测 |
| Codex | 必测 | 不要求 | 必测 | 必测 |
| Chrome/Edge 文本框 | 必测 | 不要求 | 必测 | 必测 |
| 微信/飞书输入框 | 必测 | 不要求 | 必测 | 必测 |

每个组合验证：右 Alt 是否生效、胶囊是否抢焦点、是否只插入一次、失败是否保留剪贴板、历史是否可复制。

## 8. 完成定义

- 主流程在 360 开启时仍至少能够完成录音、识别、保存历史和一键复制。
- 无输入目标时仍可完成录音与识别，并以正常完成状态保存历史。
- 支持的目标控件可自动写入；不支持时给出明确但不打扰的降级提示。
- 不丢录音、不丢最终文本、不重复插入。
- 不泄露阿里云凭证和用户剪贴板内容。
- UI 与设计稿、令牌和状态说明一致。

## 9. 给下一台 Codex 的启动指令

```text
请先完整阅读 README.md、docs/prd/PRD-001.md、design/DESIGN_SYSTEM.md、
design/tokens.json 和 docs/DEVELOPMENT_HANDOFF.md。

从 M1 开始实现 Windows 11 x64 的 .NET 10 WPF 应用。严格使用设计令牌，
不要创建网页端，不要使用 Electron，不要接入本地 Whisper 或大模型改写。
自动写入必须实现交接文档中的分层策略，不允许只用 SendInput(Ctrl+V)。
每完成一个里程碑先运行测试并提交独立 commit，再进入下一个里程碑。
```

## 10. 实施记录

### 2026-07-13 M1 静态壳与视觉验收

- 已建立 .NET 10 WPF 四层解决方案：`Zumingtalk.App`、`Zumingtalk.Application`、`Zumingtalk.Domain`、`Zumingtalk.Infrastructure`，并添加 `Zumingtalk.UnitTests`。
- 已使用 `prototype/` 固定模拟数据还原首页、设置页、更多菜单、详情抽屉、Toast、兼容性提示和独立 `OverlayWindow` 胶囊。
- `OverlayWindow` 已独立于 `MainWindow`，设置为 Topmost、NoActivate、ToolWindow、Transparent，空闲时隐藏。
- 已加入只识别右 Alt 与 Esc 的全局低级键盘钩子骨架，用于 M1 模拟胶囊状态；真实录音、阿里云实时 ASR、SQLite 和分层写入将在 M2-M4 接入。
- 已定义阿里云 ASR、录音、全局热键和文本写入接口，并保留 `EM_REPLACESEL`、`WM_PASTE`、`SendInput`、复制兜底的分层写入模型。
- 已验证 `npm run build`、prototype 可点击交互、`dotnet build Zumingtalk.sln`、`dotnet test Zumingtalk.sln --no-build` 和 WPF 启动烟测。
