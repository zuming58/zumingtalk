# 祖名闪电说 v0.5.1 验收报告

日期：2026-07-13

## 自动测试

- `dotnet build Zumingtalk.sln`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln --no-build`：通过，21 passed，0 failed。
- `dotnet publish src\Zumingtalk.App\Zumingtalk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`：通过。

覆盖范围：
- RegisterHotKey 与低级键盘钩子解耦；即使右 Alt 钩子失败，备用 `Ctrl + Win + Space` 仍独立注册，并在设置页显示主/备用注册结果。
- 写入前二次调用 `GetGUIThreadInfo` 校验当前焦点控件；同一窗口内焦点变化会判定为 Lost。
- 每次听写独立 `CancellationTokenSource`；Esc 会取消并等待 ASR 启动任务，取消后创建出的 ASR session 会立即关闭。
- Lost 只保存历史，不覆盖剪贴板；只有明确的自动写入失败才走复制兜底。
- 平均语速按成功时长真实计算；重新转写会修正统计；历史记录支持“昨天”分组。
- 回到顶部按钮仅在历史列表滚动超过阈值后显示。
- v0.5.1 发布程序版本信息：ProductVersion `0.5.1`，FileVersion `0.5.1.0`。

## 发布产物

- 本地发布目录：`artifacts/publish/win-x64`
- 本地压缩包：`artifacts/publish/Zumingtalk-v0.5.1-win-x64.zip`
- SHA-256：`7861D83AB31CB429190956F04D4433837881C3D7BB6F63A62E3675032DB8D5E1`

## 仍需用户人工测试

以下项目依赖用户本机真实环境，不能用 Mock 冒充：
- 真实阿里云 AppKey / AccessKey ID / AccessKey Secret：Token 获取、实时 ASR 建连、开始/停止、失败重试、整段音频兜底重转写。
- 真实麦克风：右 Alt 开始后录音和胶囊立即出现，不等待阿里云建连；WAV 保存与播放。
- 跨应用写入：记事本、浏览器、VS Code、Codex、微信/飞书输入框。
- Lost 行为：原输入目标丢失或同窗口焦点变化时，只保存历史且不覆盖剪贴板。
- 权限与安全软件矩阵：管理员应用、360 开启/关闭、右 Alt 被拦截时备用热键可用。

## 安全边界

- 未提交阿里云凭证、Token、录音、SQLite 数据库、日志或构建产物。
- 不使用本地 Whisper。
- 不接入大模型改写。
- 不使用 Electron 或网页端作为正式产品。