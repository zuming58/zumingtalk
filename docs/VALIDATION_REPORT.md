# 祖名闪电说 V1 验收报告

日期：2026-07-13

## 自动测试

- `dotnet build Zumingtalk.sln`：通过，0 warning，0 error。
- `dotnet test Zumingtalk.sln --no-build`：通过，8 passed，0 failed。

覆盖范围：

- M2 SQLite 记录持久化、统计累计、三天录音清理、阿里云 Secret 当前用户加密保存。
- M2 真实麦克风录音和 WAV 播放服务可编译接入；录音设备需人工运行验证。
- M3 右 Alt/Esc 管线、PCM 分片事件、阿里云无凭证失败路径。
- M4 无输入目标不写入、可编辑目标触发写入服务的应用层路径。

## 需要用户人工测试

以下项目需要在用户 Windows 11 x64 机器上使用真实阿里云凭证、真实麦克风、目标应用和安全软件环境执行：

- 阿里云实时语音识别：填写 AppKey、AccessKey ID、AccessKey Secret 后，确认 Token 获取、WebSocket 开始/停止识别、Token 过期重试一次。
- 录音：右 Alt 开始，声波跟随麦克风音量；再次右 Alt 停止，WAV 保存在本机历史目录；Esc 取消不生成记录。
- 10 分钟上限：连续录音达到上限后自动停止并识别。
- 写入矩阵：记事本、浏览器输入框、VS Code、Codex、微信/飞书输入框。
- 权限矩阵：普通权限、管理员目标应用、360 开启、360 关闭。
- 兼容行为：无输入目标和目标丢失时只保存历史，不覆盖剪贴板；自动写入失败时最终文本保留在剪贴板。

## 发布

- 命令：`scripts/publish-win-x64.ps1`
- 输出目录：`artifacts/publish/win-x64`
- 主程序：`artifacts/publish/win-x64/Zumingtalk.App.exe`
- SHA-256：`31E3AA67971A39D3584B7CA215B1BE0700E0FC3F3B0F46617AD50E0647D8D494`

## 安全边界

- 未提交阿里云凭证、Token、录音、SQLite 数据库、日志或发布产物。
- 不使用本地 Whisper。
- 不接入大模型改写。
- 不使用 Electron 或网页端作为正式产品。
