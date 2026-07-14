# 祖名闪电说 0.6.1 修复交接文档

> 日期：2026-07-13  
> 交接目标：让另一台电脑上的 Codex 接手“悬浮胶囊不显示、Codex 输入框无法自动写入、设置页控件错位”三项修复，并完成真实环境验收和发布。  
> 当前状态：已停止继续开发；本机存在未提交的初步修复，尚未通过 .NET 10 编译、真实录音或视觉验收，不能直接当成完成版本发布。

## 1. 先看这里：仓库和工作区状态

- 仓库：`zuming58/zumingtalk`
- 本机目录：`F:\Codex\talktalk`
- 当前分支：`main`
- 当前基准提交：`dd1c996056efc46a49cefce777b5daa7b1fc6870`（`Fix v0.5.1 audit blockers`）
- 当前项目版本号：`0.6.0`
- 当前工作树不是干净状态：有 20 个已修改文件、2 个新增文件。
- 这些未提交内容同时包含：
  1. 从旧阿里云智能语音交互接口迁移到百炼 `fun-asr-realtime` 的改动；
  2. 本文三项问题的初步修复；
  3. 相应文档和单元测试调整。
- **禁止执行** `git reset --hard`、`git checkout -- .`、`git clean -fd`，也不要直接用远端 `main` 覆盖本机工作树。
- 如果接手者在另一台电脑上只克隆 GitHub 的 `main`，将看不到本机这些未提交改动。接手前必须先确认代码来源：
  - 若已将本机改动提交到一个交接分支，直接检出该分支；
  - 若尚未提交，则需要把本机工作树补丁或完整目录传过去；
  - 如果只能从 `dd1c996` 开始，需按本文第 5～8 节重新实现，不要误以为远端已经包含 0.6.0 改动。

当前改动文件清单：

```text
README.md
START_HERE.md
docs/DEVELOPMENT_HANDOFF.md
docs/VALIDATION_REPORT.md
docs/prd/PRD-001.md
docs/BAILIAN_SETUP.md                         新增
src/Zumingtalk.App/App.xaml
src/Zumingtalk.App/MainWindow.xaml
src/Zumingtalk.App/MainWindow.xaml.cs
src/Zumingtalk.App/Windows/OverlayWindow.xaml
src/Zumingtalk.App/Windows/OverlayWindow.xaml.cs
src/Zumingtalk.App/Zumingtalk.App.csproj
src/Zumingtalk.Application/DesignTime/MockDataFactory.cs
src/Zumingtalk.Application/Shell/ShellViewModel.cs
src/Zumingtalk.Domain/Services/IAsrProviderFactory.cs
src/Zumingtalk.Domain/Services/ISettingsRepository.cs
src/Zumingtalk.Domain/Settings/AppSettings.cs
src/Zumingtalk.Infrastructure/Asr/BailianFunAsrProvider.cs   新增
src/Zumingtalk.Infrastructure/Storage/SqliteStore.cs
src/Zumingtalk.Infrastructure/Windows/WindowsTextInsertionService.cs
tests/Zumingtalk.UnitTests/DictationCoordinatorTests.cs
tests/Zumingtalk.UnitTests/SqliteStoreTests.cs
```

## 2. 用户刚刚实际验收发现的问题

### 2.1 右 Alt 能录音和转写，但悬浮胶囊完全看不到

用户实际操作：

1. 光标放在记事本或其他应用输入框；
2. 短按右 Alt；
3. 录音、百炼识别和记事本写入可以完成；
4. 录音期间屏幕底部没有出现之前设计的玻璃胶囊。

期望行为：

- 胶囊是独立 `OverlayWindow`，不是主窗口里的控件；
- 主窗口最小化到后台后，在任意前台应用按右 Alt 都应显示；
- 位于当前前台窗口所在显示器的工作区底部居中，距任务栏约 12 DIP；
- 204×58 DIP、白色玻璃质感、始终置顶、不抢焦点、不出现在任务栏；
- 录音中显示“直接说”和实时声波；识别中显示“识别中”和三个点；完成、保存、受阻或失败短暂提示后消失；
- Esc 取消后立即消失；空闲时不保留透明占位层。

相关用户截图：

- `C:\Users\ADMINI~1\AppData\Local\Temp\codex-clipboard-4a557e58-a8a9-462b-890d-b0bf771d4427.png`
- 胶囊视觉参考来自此前确认稿：紧凑的白色玻璃胶囊，文字与蓝紫色声波，中间竖向分隔线。

### 2.2 记事本能写入，Codex 对话输入框不能写入

用户已经确认：

- Windows 记事本输入框可以自动插入最终文字；
- Codex 桌面应用的对话输入框无法自动插入；
- 识别本身已成功，问题发生在最终文字写入阶段。

Codex 桌面输入框属于 Chromium/Electron 类窗口，不能使用原生 `EM_REPLACESEL`，也不能可靠使用 `WM_PASTE + WM_GETTEXTLENGTH` 验证。目标路径应该是：

1. 开始录音时捕获前台顶层窗口、焦点窗口、进程和窗口类名；
2. 悬浮胶囊全程 `NoActivate`，不能改变焦点；
3. 结束识别时验证原目标仍存在、仍是前台窗口且焦点句柄未变；
4. 对 `Chrome`、`WebView`、`HwndWrapper`、`Internet Explorer_Server` 类窗口：
   - 将最终文字写入剪贴板；
   - 等待约 50～100 ms；
   - 使用 `SendInput` 发送完整的 Ctrl 按下、V 按下、V 抬起、Ctrl 抬起四个事件；
   - 不要立即恢复原剪贴板，因为 Chromium 可能异步读取剪贴板；
   - 最终文字继续留在剪贴板，作为失败时的手动粘贴兜底；
   - `SendInput` 返回 4 只能证明按键事件已送入系统，不能百分之百证明目标编辑器已经接收，因此 UI 文案不能虚假承诺“已验证成功”。
5. 管理员权限目标或 360 阻挡时不得绕过安全软件、注入 DLL、安装驱动或强制提权；只保留文字到剪贴板和历史记录。

### 2.3 设置页多处控件文字偏上、挤出按钮

用户指出的位置：

- “服务商”下拉框太高，文字偏上；
- “输入设备”麦克风下拉框文字偏上；
- “写入模式 / 自动选择”下拉框文字偏上；
- 右上角“保存”按钮太窄，图标和文字拥挤；
- “测试连接与麦克风”“测试自动写入”“演示写入受阻”按钮文字接近或超出边界；
- “兼容模式已启用”胶囊布局拥挤；
- 设置页出现不应有的虚线焦点边框。

相关用户截图：

- `C:\Users\ADMINI~1\AppData\Local\Temp\codex-clipboard-4a557e58-a8a9-462b-890d-b0bf771d4427.png`
- `C:\Users\ADMINI~1\AppData\Local\Temp\codex-clipboard-e60c9a51-db32-4a1b-bfa2-e2cd751bea23.png`
- `C:\Users\ADMINI~1\AppData\Local\Temp\codex-clipboard-00d2d796-2809-4dce-a7b9-42e3a97310b3.png`

## 3. 已有能力不要破坏

用户当前运行版本已经证明以下能力至少在该机器上可用：

- 百炼 Fun-ASR API Key 可以保存并完成连接测试；
- 测试提示为“百炼 Fun-ASR 连接与麦克风测试通过”；
- 右 Alt 可以开始/结束一次听写；
- 真实麦克风录音和中文转写可完成；
- 最终文字可以写入记事本；
- 设置页可以识别当前麦克风；
- 历史记录和复制兜底仍应保留。

修复时不要退回本地 Whisper，不要切回旧 AccessKey/AppKey/Token 接口，也不要加入大模型改写。

## 4. 本机已做但尚未验证的初步修复

以下内容已经写入本机工作树，但因为本机只有 .NET SDK 6.0.301，无法编译目标为 `net10.0-windows` 的项目。接手者必须自行审查，而不是默认正确。

### 4.1 悬浮胶囊 DPI 定位初步修复

涉及文件：

- `src/Zumingtalk.App/Windows/OverlayWindow.xaml`
- `src/Zumingtalk.App/Windows/OverlayWindow.xaml.cs`
- `src/Zumingtalk.App/MainWindow.xaml.cs`

已经尝试：

- `OverlayWindow` 增加 `WindowStartupLocation="Manual"`；
- 新增 `PhysicalWorkArea`，保存 `GetMonitorInfo` 返回的物理像素工作区；
- 新增 `PositionOverWorkArea(...)`，根据目标窗口 DPI 把 204×58 DIP 换算成物理像素；
- 使用 `SetWindowPos(HWND_TOPMOST, ..., SWP_NOACTIVATE | SWP_NOOWNERZORDER)` 定位；
- 显示前定位一次，`Show()` 后再定位一次；
- DPI 使用前台目标窗口的 `GetDpiForWindow`。

初步根因判断：旧实现把 Win32 返回的物理像素直接赋值给 WPF 的 DIP `Left/Top`，在 125% 或 150% 缩放下可能把胶囊放到屏幕可视区域之外。

接手者仍需确认：

- 应用是否声明 `PerMonitorV2` DPI 感知；
- `SetWindowPos` 后 WPF 是否又根据 `Left/Top` 重排；
- 单显示器、主/副显示器、不同缩放混用、任务栏位于底部/侧边时的位置；
- `Show()` 前 `EnsureHandle()` 会触发 `OnSourceInitialized`，扩展样式是否都生效；
- 胶囊是否真的不抢 Codex/浏览器输入焦点。

### 4.2 Codex/Chromium 键盘粘贴初步修复

涉及文件：

- `src/Zumingtalk.Infrastructure/Windows/WindowsTextInsertionService.cs`
- `tests/Zumingtalk.UnitTests/DictationCoordinatorTests.cs`

已经尝试：

- 新增 `RequiresKeyboardPaste(className)`；
- 对 Chromium/Electron 类窗口跳过 `WM_PASTE` 和 `WM_GETTEXTLENGTH` 验证；
- 新增 `TryKeyboardClipboardPaste(text)`；
- 先保留最终文字到剪贴板，再 `SendInput(Ctrl+V)`；
- 不恢复旧剪贴板，避免 Codex 异步读取失败；
- `SendCtrlV()` 改为返回已发送事件数；
- 新增两项纯逻辑单元测试，覆盖 4 个事件成功和 0 个事件受阻。

已知风险：

- 当前逻辑把 `SendInput` 返回 4 映射为 `Succeeded=true`，这只是“事件成功提交”，不是“Codex 已写入”的强验证。建议把用户可见状态改成“已尝试写入”，同时始终保留剪贴板兜底；不要显示误导性的“已确认成功”。
- `Clipboard.SetText` 可能遇到剪贴板占用；应复用或补充最多 3 次的短延迟重试，不应因一次 `ExternalException` 丢失最终文本。
- `Thread.Sleep` 当前在调用线程执行，可能短暂阻塞 UI；建议改为异步等待，或明确控制总等待不超过约 300 ms。
- 真实 Codex 输入框必须人工验收；单元测试不能代替。
- 若目标在识别期间失焦，按照原产品规则应保存历史而不是猜测新目标并粘贴。

### 4.3 设置页控件初步修复

涉及文件：

- `src/Zumingtalk.App/App.xaml`
- `src/Zumingtalk.App/MainWindow.xaml`
- `src/Zumingtalk.Application/Shell/ShellViewModel.cs`
- `src/Zumingtalk.App/MainWindow.xaml.cs`

已经尝试：

- 给 `ComboBox` 和 `ComboBoxItem` 添加统一高度、左右内边距、垂直居中和字号；
- 修复 `PrimaryButtonStyle` 模板未把 `Padding` 传给内部 `Border` 的问题；
- “保存”按钮最小宽度改为 116；
- “测试连接与麦克风”最小宽度改为 174；
- “测试自动写入”最小宽度改为 136；
- “演示写入受阻”最小宽度改为 148；
- 写入模式下拉框宽度改为 172；
- 兼容模式徽标增加最小宽度和固定高度，文字水平/垂直居中；
- 设置页 `ScrollViewer` 设为 `Focusable=false`，用于去除虚线焦点框；
- 最近目标、最近写入方式和结果开始绑定真实运行时状态，不再硬编码“已成功 / 22:18”。

接手者仍需完成视觉 QA：

- Windows 100%、125%、150% 缩放；
- 至少 1920×1080 和 2560×1440；
- 中文文字不裁切，按钮左右至少 18 DIP 内边距；
- 所有单行控件视觉中心一致；
- 下拉箭头和文字不互相挤压；
- 键盘焦点可见但不出现贯穿整个页面的异常虚线框；
- 不要只靠固定宽度掩盖问题，应检查模板、`Padding`、`VerticalContentAlignment` 和父布局。

### 4.4 运行时状态显示初步修复

已经增加：

- `ShellViewModel.UpdateCapturedTarget(...)`
- `ShellViewModel.UpdateInsertionResult(...)`
- `LastInsertionMethodText`
- `LastInsertionStatusText`

接手者需要确认：

- 更新 `Settings` 记录后，所有绑定均能收到 `PropertyChanged`；
- 是否需要将最近目标/最近写入状态持久化到 SQLite；
- “尚未捕获”“尚未写入”“已尝试写入”“已复制兜底”等文案与真实状态一致。

## 5. 推荐接手顺序

### 第一步：恢复可编译环境

必须安装或使用 .NET 10 SDK。先执行：

```powershell
dotnet --version
dotnet --list-sdks
```

需要看到 `10.0.x`。本机当前默认只有 `6.0.301`，因此本次停工前执行：

```powershell
dotnet build Zumingtalk.sln -c Release --no-incremental
```

得到 `NETSDK1045`，原因只是 SDK 版本不支持 .NET 10；本次没有得到有效的代码编译结果。

在另一台装好 .NET 10 的电脑上执行：

```powershell
dotnet restore Zumingtalk.sln
dotnet build Zumingtalk.sln -c Release --no-incremental
dotnet test Zumingtalk.sln -c Release --no-build
```

先修复所有编译错误和警告，再进入真实 UI 测试。

### 第二步：先验证悬浮胶囊，不要先改视觉

1. 启动 WPF 应用；
2. 最小化主窗口；
3. 在记事本输入框按右 Alt；
4. 确认底部胶囊出现、声波跳动、输入焦点仍在记事本；
5. 按 Esc，确认立即消失且不产生记录；
6. 再开始一次，结束后确认“识别中”状态可见；
7. 在 100%、125%、150% 三档缩放重复；
8. 有副显示器时，把前台输入框移动到副屏，确认胶囊跟随目标屏幕。

若仍不显示，按以下顺序查：

1. `ShellViewModel.OverlayState` 是否从 `Idle` 变为 `Recording`；
2. `OnViewModelPropertyChanged` 是否调用 `SyncOverlayWindow()`；
3. `overlayWindow.Show()` 是否执行；
4. `OverlayWindow.IsVisible`、Win32 HWND、最终物理坐标和尺寸；
5. 是否被设置到屏幕外；
6. 是否被 `Hide()` 定时器立即隐藏；
7. 是否存在 WPF Dispatcher 跨线程异常；
8. Topmost/NoActivate 扩展样式是否生效。

### 第三步：单独完成 Codex 写入

不要拿“测试自动写入”按钮直接判断外部应用，因为点击该按钮本身会把焦点切回祖名闪电说。应使用真实流程：

1. 主窗口最小化；
2. 光标放在 Codex 对话输入框；
3. 右 Alt 开始；
4. 说一段唯一测试文本，例如“祖名闪电说七月十三日测试”；
5. 右 Alt 结束；
6. 确认文本只写入一次；
7. 确认没有自动发送消息；
8. 若未写入，立即用 Ctrl+V，确认剪贴板仍保存最终文字；
9. 记录 `CapturedInputTarget` 的进程名、类名、顶层窗口句柄、焦点句柄和完整性级别，但日志中不要记录转写全文或旧剪贴板内容。

同时测试：

- Codex 桌面应用；
- Chrome/Edge 网页输入框；
- VS Code；
- 记事本；
- 开启 360 后的同一组测试；
- 管理员权限目标：应降级复制，不要求自动注入。

### 第四步：设置页统一控件系统

不要逐个按钮随意加宽。先在 `App.xaml` 统一：

- 主按钮、次按钮的模板必须传递 `Padding`；
- `MinHeight=42` 或 44；
- `HorizontalContentAlignment=Center`；
- `VerticalContentAlignment=Center`；
- 文本按钮按内容给 `MinWidth`；
- ComboBox 单行高度 44、文字垂直居中、下拉项最小高度 36；
- 然后只在页面上设置确有必要的宽度。

完成后用用户三张截图做前后对照，逐项检查裁切、偏上、留白和焦点框。

### 第五步：完整回归并发布 0.6.1

发布前至少满足：

```powershell
dotnet build Zumingtalk.sln -c Release --no-incremental
dotnet test Zumingtalk.sln -c Release --no-build
dotnet publish src/Zumingtalk.App/Zumingtalk.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish/v0.6.1-win-x64
```

然后：

- 项目版本改为 `0.6.1` / `0.6.1.0`；
- 压缩为 `artifacts/publish/Zumingtalk-v0.6.1-win-x64.zip`；
- 计算 SHA-256；
- 更新 `docs/VALIDATION_REPORT.md`；
- 不要把 API Key、数据库、录音、`bin/`、`obj/` 或用户配置打入发布包；
- 真实人工验收通过后再 commit、push、打 tag 和创建 GitHub Release。

## 6. 验收清单

### 悬浮胶囊

- [ ] 主窗口最小化时，右 Alt 仍能显示胶囊。
- [ ] 胶囊出现在当前目标显示器工作区底部居中。
- [ ] 100%、125%、150% 缩放均可见。
- [ ] 录音中声波随麦克风音量变化。
- [ ] 识别中状态可见。
- [ ] 胶囊不抢焦点，不阻断 Codex/浏览器输入框。
- [ ] Esc 取消后立即消失且不保存记录。
- [ ] 完成、保存、写入受阻、失败状态结束后自动隐藏。

### 自动写入

- [ ] 记事本仍使用原生插入且只写一次。
- [ ] Codex 对话输入框可写入且不会自动发送。
- [ ] Chrome/Edge 输入框可写入。
- [ ] VS Code 可写入。
- [ ] 写入无法确认时，最终文字留在剪贴板。
- [ ] 目标丢失时只保存历史，不猜测新目标。
- [ ] 管理员权限目标降级复制。
- [ ] 360 阻挡时不绕过安全软件，仍保留历史和复制兜底。

### 设置页

- [ ] 服务商、麦克风、写入模式三个下拉框文字垂直居中。
- [ ] 保存按钮图标与文字不拥挤。
- [ ] 三个测试/演示按钮文字不裁切。
- [ ] 兼容模式徽标不裁切。
- [ ] 页面没有异常贯穿虚线焦点框。
- [ ] 100%、125%、150% 缩放通过。
- [ ] 最近目标和最近写入状态不再显示硬编码假数据。

### 百炼与数据安全

- [ ] 百炼 `fun-asr-realtime` 真实连接通过。
- [ ] API Key 使用 Windows 用户级加密保存。
- [ ] API Key 不出现在数据库明文、日志、崩溃信息或发布包。
- [ ] 真实录音、历史记录、重转写、复制和清理策略回归通过。

## 7. 不要做的事情

- 不要改成本地 Whisper。
- 不要加入大模型总结、润色或改写。
- 不要为了 360 兼容去关闭防护、注入 DLL、安装驱动、绕过防护或让程序默认以管理员身份运行。
- 不要在 Codex 输入框里自动发送 Enter。
- 不要把 `SendInput` 返回成功包装成百分之百“目标已接收”的假验证。
- 不要恢复旧剪贴板过早，导致 Chromium/Electron 粘贴为空。
- 不要在没有真实 UI 验收的情况下发布 0.6.1。
- 不要清理或覆盖当前未提交工作树。

## 8. 可直接发给另一台 Codex 的指令

```text
请接手祖名闪电说仓库 zuming58/zumingtalk，完整阅读：
1. docs/HANDOFF-2026-07-13-CAPSULE-CODEX-SETTINGS.md
2. docs/prd/PRD-001.md
3. docs/DEVELOPMENT_HANDOFF.md
4. design/DESIGN_SYSTEM.md
5. docs/BAILIAN_SETUP.md

先检查 git status、当前分支和 HEAD。当前基准提交应为 dd1c996；交接工作树可能包含未提交的百炼迁移和三项初步修复，禁止 reset/checkout/clean 覆盖用户改动。如果你拿到的只是 GitHub main，请明确说明本机未提交改动不存在，再按交接文档重新实现。

本轮只完成三个用户问题：
1. 修复独立 OverlayWindow 悬浮玻璃胶囊在任意应用、不同 DPI 和多显示器下不显示的问题；
2. 修复 Codex/Chromium/Electron 输入框最终文字无法自动写入，同时保留剪贴板兜底且不自动发送；
3. 统一修复设置页 ComboBox、保存按钮、测试按钮、兼容徽标的垂直居中、内边距、宽度和焦点边框。

先装/使用 .NET 10 SDK，完成编译和单元测试；再做真实右 Alt、真实胶囊、真实 Codex 输入框和 100/125/150% 缩放人工验收。不要改回旧阿里云接口，不要使用本地 Whisper，不要加入大模型改写，不要绕过 360 或 UAC。所有验收通过后再升级到 0.6.1，发布 self-contained win-x64 包，更新验证报告并提交到独立分支供审计。

完成时请回报：
- commit SHA 和分支；
- build/test 的完整结果；
- 发布包路径和 SHA-256；
- 胶囊在 100/125/150% 的截图；
- Codex、记事本、浏览器、VS Code、360 开关矩阵的人工测试结果；
- 尚未验证或失败的项目，禁止用“全部完成”概括。
```

## 9. 本次停工结论

本次没有宣称修复完成，也没有发布新版本。只完成了问题定位和初步代码修改，并将后续实现、风险与验收要求完整交接。当前唯一有效的本机验证结果是：使用系统默认 .NET 6.0.301 编译 .NET 10 项目会报 `NETSDK1045`；这不代表代码本身编译失败或成功，另一台电脑必须使用 .NET 10 重新验证。
