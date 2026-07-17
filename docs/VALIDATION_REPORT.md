# 祖名闪电说 v0.6.4 自动写入权限修复与验收报告

日期：2026-07-17

## v0.6.4 最新结论

v0.6.3 真人测试确认 Codex 和微信均直接进入“复制兜底”。现场只读进程检查显示 `ChatGPT.exe`、`Weixin.exe` 和 `Zumingtalk.App.exe` 均运行在高完整性级别。旧代码只检查目标是否为高完整性，只要目标为高就无条件跳过 `SendInput`，没有判断本程序是否也处于同级权限，因此两次测试实际上都没有发送 `Ctrl+V`。

本轮修复：

- 仅在“本程序非高完整性、目标为高完整性”时提前降级；双方同为高完整性时正常执行键盘粘贴。
- 当前程序完整性诊断不再固定写成 `Medium`，改为读取真实进程 Token。
- `SendInput` 全量发送成功时明确把 Win32 错误码归零，避免显示上一次系统调用遗留的错误。
- 增加四种本程序/目标权限组合的回归测试。
- 程序版本更新为 `0.6.4 / 0.6.4.0`。

自动验证：

- Release 编译：0 warning、0 error。
- 单元测试：48 passed、0 failed、0 skipped。
- 传递依赖漏洞检查：未发现已知易受攻击包。
- 真人测试包：`artifacts/publish/Zumingtalk-v0.6.4-elevation-insertion-win-x64.zip`。
- EXE SHA-256：`12B69ED146C1892102C51963A99A5A88262069EC7DC3C5F94CE0F44534B71963`。
- ZIP SHA-256：`6FC7EA63AD06961A1AEED8DB39D9431DE2CEB1BC58592CCBA6F8EB4672C0209C`。

仍需真人确认 Codex 与微信在同级高完整性环境中真实接收文字；本轮不把自动测试当作最终输入验收。

---

## v0.6.3 最新结论

本轮针对真人测试中“记事本可以写入，但 Codex/Chromium 与微信不能写入”的问题修复自动写入链路：

- 修正 x64 `SendInput` 的原生结构体布局。旧实现的 `INPUT` 联合体缺少 `MOUSEINPUT` 最大成员，传给 Windows 的结构尺寸错误，依赖模拟 `Ctrl+V` 的 Chromium/Electron/Qt 应用会被系统拒绝；记事本走 `EM_REPLACESEL`，因此没有暴露此问题。
- 对 Codex/Chromium 的 UI Automation `ValuePattern`/`TextPattern` 做写入前后文本比对；能够验证文本变化时才报告“已写入”，无法验证时继续把最终文字留在剪贴板。
- 新版微信只暴露 `Weixin.exe + Qt...QWindowIcon` 顶层焦点窗口，不提供标准编辑框或插入光标。现对 `Weixin`、`WeChat`、`WXWork` 的 Qt 窗口启用受控键盘粘贴，仍要求原顶层窗口和原进程持续位于前台。
- 移除设置页中的三键备用热键，并停止注册 `Ctrl + Win + Space`；V1 仅使用右 Alt 启停录音。
- 程序版本更新为 `0.6.3 / 0.6.3.0`。

自动验证：

- `dotnet build Zumingtalk.sln -c Release --no-incremental`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln -c Release --no-build`：通过，44 passed，0 failed，0 skipped。
- `dotnet list Zumingtalk.sln package --vulnerable --include-transitive`：未发现已知易受攻击包。
- 发布目录：`artifacts/publish/v0.6.3-input-compat-win-x64`。
- 真人测试包：`artifacts/publish/Zumingtalk-v0.6.3-input-compat-win-x64.zip`。
- EXE ProductVersion：`0.6.3`；FileVersion：`0.6.3.0`。
- EXE SHA-256：`4FF2EA67A4F5930378F7293A1AE6FDCCFD76937458B3D748D1D3EFA8C8D90A8C`。
- ZIP SHA-256：`9E7EC09D5E75303F96DAE9B5CC96E0F2C7BDDA318A47B57BA9809F032FF6D3D3`。

仍需真人验收：

- 在真实 Codex 输入框中确认右 Alt 结束后只写入一次最终文字。
- 在真实微信聊天输入框中确认键盘粘贴生效；由于微信不暴露可读文本接口，程序无法可靠验证接收结果，失败时仍应保留剪贴板和历史记录。
- 分别测试 360 开启和关闭两种环境，确认右 Alt、目标前台校验和复制兜底行为。

---

# 祖名闪电说 v0.6.2 R2 审计与验收报告

日期：2026-07-14

## v0.6.2 R2 最新结论

本轮基于 `codex/v0.6.1-integrated-handoff` 的 `9d3f3e49b875d9c4aa419ae91c9b086817f2d396` 继续开发，保留百炼 `BailianFunAsrProvider.cs`，没有回退到旧 `audit/v0.6.1-capsule-codex-settings` 基线。

主审计在提交 `d233d879e530c183bc5a321be811435dd171dbaf` 基础上继续修正：

- 全局低级键盘钩子改为异步投递 UI 操作，避免同步 UI Automation 查询阻塞钩子回调。
- 捕获和写入前不再仅凭 `Chrome/WebView/HwndWrapper` 类名判定可编辑；必须是原生编辑框、存在真实插入光标，或当前 UI Automation 元素确认为可编辑候选。
- 写入前重新检查当前 UI Automation 焦点；同一顶层窗口内若已离开编辑控件则判定 Lost，不发送粘贴。
- 同一顶层窗口内焦点 HWND 变化时，如果新焦点仍是可编辑候选，则安全更新目标，改善微信/Codex 复合控件兼容性。
- `SendInput` 前再次确认原顶层窗口和进程仍在前台；等待期间目标变化则只保留剪贴板，不向新窗口写入。
- 每次新录音重置胶囊音量状态，避免短暂沿用上一次波形。
- 最终自动测试为 37 passed、0 failed、0 skipped；Release 构建 0 warning、0 error。

主审计结论：代码已达到下一轮真人测试候选标准，但微信和 Codex 是否真正接收文字仍只能由用户环境确认，不能以 `SendInput` 返回值或 Mock 测试代替。

主审计测试包：

- 发布目录：`artifacts/publish/v0.6.2-final-audit-win-x64`
- ZIP：`artifacts/publish/Zumingtalk-v0.6.2-final-audit-win-x64.zip`
- EXE ProductVersion：`0.6.2`；FileVersion：`0.6.2.0`
- EXE SHA-256：`F56A50A0FFAD10FF3995C912798AB5D54DC7DDBBD21CA503075B98F4EBB69E81`
- ZIP SHA-256：`43B7FB0C2E3B566350DA805EDFB589AE1E4C47A40E5A22B5CDA8806CE3311FAA`

已完成：

- 胶囊窗口调整为 `184×50 DIP`，静音显示 `28×2` 横线，说话波形由 RMS/dBFS、噪声门、迟滞和快攻慢释驱动。
- 状态文案统一为 `已写入 / 已保存 / 已复制 / 识别失败`；自动写入未确认时显示“已复制”，不再用“未能写入”作为最终胶囊标签。
- 写入前重新调用 `GetGUIThreadInfo`，同一顶层窗口内焦点控件变化也判定为 `Lost`；`Lost` 只保存历史，不覆盖剪贴板。
- 增加不记录文字内容的目标诊断：Win32 前台/焦点类名、UI Automation 控件类型/类名/AutomationId、Value/Text Pattern 支持、键盘修饰键状态、写入策略、SendInput 事件数和 Win32 错误码。
- 增加 UI Automation 目标识别，不按进程名白名单判断微信或 Codex；候选目标由 Win32 类、UIA 控件类型、Pattern 支持和可聚焦/启用状态共同判定。
- `Ctrl+V`/`SendInput` 前最多等待 300ms，确认 Alt、右 Alt、Ctrl、Win 等修饰键释放；若仍按下则不发送模拟按键，降级为复制兜底。
- 保留独立 `RegisterHotKey` 备用热键路径；注册结果继续显示在设置页。
- 程序版本更新为 `0.6.2 / 0.6.2.0`。

自动验证：

- `dotnet build Zumingtalk.sln -c Release --no-incremental`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln -c Release --no-build`：通过，31 passed，0 failed，0 skipped。
- 发布目录：`artifacts/publish/v0.6.2-win-x64`
- 测试包：`artifacts/publish/Zumingtalk-v0.6.2-win-x64.zip`
- ZIP SHA-256：`40ACA929A7B88A0BDD11D62173912800FF58B3F3DA0BB389C13F01750457DA99`

仍需用户真人环境复测：

- 填写真实百炼 API Key 后，验证真实麦克风、实时识别和录音保留。
- 在记事本、浏览器、VS Code、Codex、微信中分别执行短按右 Alt 听写，截图设置页“最近目标”诊断信息。
- 分别在 360 开启/关闭状态下验证主热键、备用热键、自动写入、复制兜底和历史保存。
- 对微信/Codex：若仍无法写入，应确认胶囊最终状态为“已复制”或“已保存”，历史记录存在，剪贴板行为符合 PRD。

---

# 祖名闪电说 v0.6.1 审计与验收报告

日期：2026-07-14

## 审计结论

当前代码可以运行，但 2026-07-14 第一轮真实环境测试已经发现阻断项，因此 v0.6.1 未通过最终验收，不能作为完成版发布。下一轮开发按 `docs/HANDOFF-2026-07-14-REAL-TEST-R2.md` 执行。

本轮没有直接合并 `audit/v0.6.1-capsule-codex-settings`，因为该分支基于 v0.5.1，不包含当前工作树的百炼 Fun-ASR 迁移。审计后只迁入了其中适用于当前架构的胶囊定位、Chromium/Codex 写入与设置页修复，避免覆盖现有功能。

## 自动验证

- `.NET SDK 10.0.301`：使用官方 SDK 完成构建和发布。
- `dotnet build Zumingtalk.sln -c Release --no-incremental`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln -c Release --no-build`：通过，26 passed，0 failed，0 skipped。
- `dotnet list Zumingtalk.sln package --vulnerable --include-transitive`：未发现已知易受攻击包。
- `git diff --check`：通过，无空白错误。
- 源码敏感信息扫描：未发现百炼 API Key 或阿里云 AccessKey 明文。
- 自包含 `win-x64` 发布：通过；目标电脑无需预装 .NET。

## 本轮重点修复

- 胶囊窗口使用 `Topmost + NoActivate + SetWindowPos`，启用 Per-Monitor V2 DPI，并按目标显示器工作区进行物理像素定位。
- 胶囊仅在录音、识别和失败状态出现，不属于主窗口页面，也不抢走输入焦点。
- Chromium、Electron、Codex 类输入框采用保留最终文字到剪贴板后发送 `Ctrl+V` 的兼容路径；只能确认按键已发送，不能伪报“目标已经收到”。
- 原目标丢失或焦点变化时不再写入错误窗口；文字保留在历史中，必要时复制兜底。
- 剪贴板写入增加短暂占用重试，降低安全软件或其他程序竞争导致的失败率。
- 设置页统一下拉框、按钮和徽标的高度、内边距及垂直居中；保存、测试写入等按钮不再裁字。
- “测试自动写入”增加 3 秒切换窗口倒计时，并根据真实结果显示“已确认写入 / 已尝试写入 / 已复制”，不再假报成功。
- 百炼 WebSocket 按完整消息累积原始字节后再解码 UTF-8，避免分片恰好切在中文多字节字符中间时出现乱码。
- 修复 PCM `short.MinValue` 音量峰值计算溢出，并补齐录音启动失败时的资源与临时文件清理。
- 识别失败时仍保留已经完成的录音路径与时长，便于历史页重新转写。

## 发布产物

- 发布目录：`artifacts/publish/v0.6.1-win-x64`
- 压缩包：`artifacts/publish/Zumingtalk-v0.6.1-win-x64.zip`
- EXE ProductVersion：`0.6.1`；FileVersion：`0.6.1.0`。
- EXE SHA-256：`A4469C647F43C1145D321224A4F6AFF92E313D1AC986802EF846291DF43559C8`
- ZIP SHA-256：`3439838D610D375A77114359F286BE03DDE797C79BC093AD6AB008302D8963AA`

## 下一轮人工验收

以下项目依赖真实桌面环境，禁止用 Mock 或“按键发送成功”代替结果：

1. 在设置页填写真实百炼 API Key，选择真实麦克风，测试连接和输入音量。
2. 将光标放到记事本，短按右 Alt 开始：胶囊应立即出现在当前显示器底部且不抢焦点；再次短按结束，最终文字只写入一次。
3. 依次在浏览器、VS Code、Codex、微信或飞书输入框中重复测试；Codex 必须人工确认文字是否真正出现。
4. 在 Windows 100%、125%、150% 缩放及多显示器上检查胶囊底部居中、尺寸和清晰度。
5. 开启 360 后测试右 Alt；若主热键被拦截，使用 `Ctrl + Win + Space`。自动写入受阻时应保留历史和剪贴板兜底。
6. 测试无输入框、原目标丢失、管理员权限应用和网络中断；不得把文字写入错误窗口，录音和失败记录应可重新转写。
7. 在历史页验证复制、播放、重新转写、详情、删除与 5 秒撤销。

## 2026-07-14 第一轮真人测试结果

- 记事本：自动写入成功。
- 微信：没有自动写入，胶囊显示“已保存”；说明当前实现没有把微信输入区识别为可编辑目标。
- Codex：没有自动写入，胶囊显示“未能写入”；说明键盘粘贴路径进入但未被目标接受。
- 360 开启和关闭结果一致，因此本轮主要阻断项不是 360。
- 微信和 Codex 的转写结果均正确进入历史，可手动复制；ASR 和持久化主流程正常。
- 胶囊偏大；音量波动不明显；静音时仍显示柱形，不符合“静音横线、说话明显波动”的要求。

结论：暂停扩大测试范围，先完成 v0.6.2 的目标诊断、UI Automation 识别、粘贴兼容、状态文案和声波视觉修复。

## 安全边界

- 百炼 API Key 使用 Windows 当前用户级 DPAPI 加密保存，不写入日志和界面明文。
- 未将 API Key、真实录音、SQLite 数据库、日志或构建产物纳入源码提交。
- 不使用本地 Whisper，不接入大模型改写，不使用 DLL 注入、驱动或绕过 360/UAC。
- 第三方程序是否接受模拟粘贴无法由本程序统一证明，因此始终保留历史复制兜底。
