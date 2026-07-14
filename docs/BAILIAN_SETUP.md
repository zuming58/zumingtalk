# 阿里云百炼 Fun-ASR 配置与测试

祖名闪电说 v0.6.1 使用阿里云百炼 `fun-asr-realtime`。不需要智能语音交互 2.0 的 AppKey、AccessKey ID 或 AccessKey Secret。

## 1. 创建 API Key

1. 登录[阿里云百炼控制台](https://bailian.console.aliyun.com/)。
2. 右上角地域选择 **华北 2（北京）**。
3. 进入 [API Key 管理](https://bailian.console.aliyun.com/?tab=model#/api-key)，单击“创建 API Key”。
4. 归属业务空间选择“默认业务空间”，个人使用可选择全部模型权限。
5. 创建后立即复制完整 API Key。关闭弹窗后无法再次查看明文；不要把它发送给他人或提交到 Git。

官方说明：[如何获取 API Key](https://help.aliyun.com/zh/model-studio/get-api-key/)。

## 2. 配置祖名闪电说

1. 解压 Windows x64 发布包，运行 `Zumingtalk.App.exe`。
2. 打开“设置”。
3. 在“百炼 API Key”中粘贴刚创建的 Key。
4. 选择麦克风，保持“语义断句”开启。
5. 单击“测试连接与麦克风”。
6. 看到“百炼 Fun-ASR 连接与麦克风测试通过”后保存设置。

API Key 使用 Windows 当前用户级 DPAPI 加密后写入本机 SQLite；界面、日志和仓库均不保存明文。

## 3. 首次听写测试

1. 打开记事本，把光标放进编辑区。
2. 短按右 Alt，确认屏幕底部出现“直接说”悬浮胶囊。
3. 说一段 5～10 秒的中文，再次短按右 Alt。
4. 等待“识别中”消失，确认文字写入记事本且首页新增历史记录。
5. 再测试浏览器、VS Code、Codex 和聊天软件输入框。

如果右 Alt 被 360 或其他软件拦截，可在设置页启用固定备用热键 `Ctrl + Win + Space`。自动写入失败时，识别结果仍保存在历史中；使用卡片上的复制按钮即可。

## 4. 费用与模型

- 固定模型：`fun-asr-realtime`（稳定别名）。
- 中国内地当前公开价：0.00033 元/秒，约 1.188 元/小时。
- 新开通百炼后 90 天内提供 36,000 秒（10 小时）免费额度；实际费用和活动以阿里云账单为准。

官方说明：[百炼模型价格](https://help.aliyun.com/zh/model-studio/model-pricing)、[实时语音识别](https://help.aliyun.com/zh/model-studio/real-time-speech-recognition-user-guide)。
