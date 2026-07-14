# 祖名闪电说 v0.6.1 验收报告

日期：2026-07-14

## 代码来源说明

当前本机仓库位于 `F:\Codex\zumingtalk\zumingtalk-main`，基准为 `dd1c996056efc46a49cefce777b5daa7b1fc6870`。交接文档中提到的另一台机器未提交 0.6.0 工作树没有出现在当前仓库，因此本次从 v0.5.1 重新实现三项修复：悬浮胶囊显示、Chromium/Codex 写入、设置页控件对齐。

## 自动验证

- `dotnet build Zumingtalk.sln -c Release --no-incremental`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln -c Release --no-build`：通过，23 passed，0 failed。
- `dotnet publish src\Zumingtalk.App\Zumingtalk.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\v0.6.1-win-x64`：通过。
- 发布程序版本：ProductVersion `0.6.1`，FileVersion `0.6.1.0`。

## 已修复范围

- OverlayWindow 改为手动定位，并通过 `SetWindowPos(HWND_TOPMOST, ... SWP_NOACTIVATE)` 按前台窗口 DPI 与显示器物理工作区定位，避免 WPF DIP 与 Win32 物理像素混用导致胶囊跑到屏幕外。
- 应用声明 `ApplicationHighDpiMode=PerMonitorV2`，支持 100%/125%/150% 缩放下的 DPI 感知定位。
- Chromium/Electron/WebView/HwndWrapper/Internet Explorer_Server 类目标不再走 `WM_PASTE + WM_GETTEXTLENGTH` 强验证；改为保留最终文本到剪贴板后发送完整 Ctrl+V，并返回“已尝试写入”，不伪装成已确认成功。
- 剪贴板写入增加短重试，避免一次剪贴板占用导致最终文本丢失。
- 设置页统一 ComboBox 与按钮垂直居中、Padding、MinWidth；保存、测试连接、测试写入、演示写入受阻和兼容模式徽标尺寸已调整。
- 最近目标、最近写入方式和写入状态改为运行时真实绑定，不再显示硬编码假数据。

## 本机视觉/启动检查

- Release 程序可启动，主窗口标题正常显示。
- 已生成主窗口截图：`artifacts/screenshots/v0.6.1-main.png`。
- 当前自动化点击未能可靠切换到设置页截图，因此设置页 100%/125%/150% 视觉 QA 仍需人工确认。

## 发布产物

- 本地发布目录：`artifacts/publish/v0.6.1-win-x64`
- 本地压缩包：`artifacts/publish/Zumingtalk-v0.6.1-win-x64.zip`
- SHA-256：`26668B81E3825B3B30696BD5C938FB459C5EE69629D8A301FC5FE6D1D971E5E0`

## 仍需用户人工测试

以下项目依赖真实桌面、目标应用、安全软件和凭证环境，未用 Mock 冒充完成：

- 右 Alt 启动后悬浮胶囊在主窗口最小化、任意前台应用、多显示器和 100%/125%/150% 缩放下可见，且不抢焦点。
- Esc 取消后胶囊立即消失且不保存记录。
- Codex 桌面输入框可写入一次，不自动发送；若未写入，Ctrl+V 可粘贴最终文本。
- Chrome/Edge、VS Code、记事本写入矩阵。
- 管理员权限目标降级复制，不绕过 UAC。
- 360 开启/关闭矩阵，不绕过安全软件，失败时保留历史和剪贴板兜底。
- 设置页三张用户截图对应的控件视觉 QA：下拉文字垂直居中、按钮不裁切、徽标不挤压、无异常贯穿虚线焦点框。
- 当前仓库仍使用 v0.5.1 的阿里云 ASR 实现；交接文档所述百炼 Fun-ASR 未提交工作树未在本机出现，未在本次实现中迁移。

## 安全边界

- 未提交 API Key、Token、录音、SQLite 数据库、日志、bin/obj 或发布产物。
- 不使用本地 Whisper。
- 不接入大模型改写。
- 不绕过 360 或 UAC。