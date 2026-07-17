# 祖名闪电说 V0.7 商业化开发交接

## 给接手 Codex 的首条指令

请在仓库 `zuming58/zumingtalk` 中从分支 `codex/v0.7-commercial-foundation` 开始开发，不要修改、重置或覆盖 `codex/v0.6.4-elevation-insertion`，更不要操作用户 D 盘正在使用的 v0.6.4 程序。当前交接分支是**文档基线**：只包含 V0.7 产品与工程约束，不包含半成品服务端代码。请先完整阅读本文件和 `docs/prd/PRD-002-commercialization.md`，再建立自己的工作分支，例如 `codex/v0.7-m1-commercial-foundation`。

## 0. 当前已验证基线（不得回退）

- 解决方案：.NET 10 WPF，当前主代码为 `Zumingtalk.App / Application / Domain / Infrastructure / UnitTests`。
- 自用识别：本机百炼 `fun-asr-realtime`，API Key 经 Windows DPAPI 加密保存。
- 现有能力：右 Alt、Esc、录音、玻璃悬浮胶囊、3 天本地历史/录音、复制兜底、分层写入、Codex/微信/360 真人兼容修复。
- 重点回归文档：`docs/VALIDATION_REPORT.md` 与 `docs/HANDOFF-2026-07-14-REAL-TEST-R2.md`。
- 不得在商业化重构中修改 `WindowsTextInsertionService`、热键钩子、OverlayWindow 行为或本地录音格式，除非有新增回归测试和真人验证。

## 1. 先做的仓库检查

```powershell
git fetch origin --prune
git switch codex/v0.7-commercial-foundation
git pull --ff-only origin codex/v0.7-commercial-foundation
git switch -c codex/v0.7-m1-commercial-foundation
git status --short
dotnet --info
dotnet build Zumingtalk.sln -c Release --no-incremental
dotnet test Zumingtalk.sln -c Release --no-build
```

如果本机未安装 .NET 10 SDK，先安装官方 .NET 10 SDK；不得把目标框架降级到 .NET 8/6 以便本机临时编译。现有 V0.6.4 的发布配置与包不得覆盖。

## 2. 必须遵守的架构和安全约束

1. 新增 `Zumingtalk.Service`（ASP.NET Core 10）和 `Zumingtalk.ServiceTests`；本地开发用 Docker PostgreSQL，生产连接串只用环境变量/密钥管理。
2. 客户端 SQLite 不是商业账本。邀请码、设备令牌哈希、权益、额度桶、结算账本、订单和支付通知必须存服务端 PostgreSQL。
3. 祖名云端百炼主 Key 只存在服务端。客户端不能收到、缓存、日志打印或反编译得到它。
4. 用户自有百炼/火山 Key 只在客户端 DPAPI 加密保存，并由客户端直接调厂商；服务端不得接收它们。
5. 服务端不得落盘音频和转写正文；结构化日志、异常、审计数据中也不得包含它们。
6. 支付私钥、支付宝公钥、Webhook 请求签名和数据库密码均不得提交。提供 `.env.example`，真实 `.env` 加入 `.gitignore`。
7. 管理员能力必须服务端鉴权，不能通过 WPF 隐藏按钮或固定客户端密钥保护。
8. 不要用 DLL 注入、驱动、关闭 360、修改鼠标/系统设置、提权或绕过 UAC 来改善自动写入；保持当前官方 Win32/UIA/剪贴板兼容路径。

## 3. M1：商业化基础

### 要实现

- `docker-compose.yml`：仅 PostgreSQL；提供非敏感示例环境变量。
- EF Core 或版本化 SQL 迁移，创建 `invite_codes`、`device_activations`、`entitlements`、`quota_buckets`、`usage_ledger`、`orders`、`payment_notifications`、`admin_audit_logs`。
- 激活 API：邀请码一次性绑定设备，返回一次设备令牌，数据库只存 Token/邀请码哈希。
- 管理 API/最小后台：创建邀请码、重置设备、手动开通 Pro、手动发放 36,000 秒加量包；全部写不含敏感内容的审计日志。
- 设备令牌鉴权与吊销；不要将管理员 Key 放进 WPF。

### 测试

- 一个邀请码只能激活一次；已绑定设备不能再生成第二个有效令牌。
- 重置设备后旧令牌无效。
- 数据库和日志扫描不到邀请码明文、设备令牌明文、Secret。
- Release build 和全部原有单测不回归。

## 4. M2：额度与祖名云端 ASR

### 要实现

- 明确三个来源：`ZumingtalkCloud`、`BailianBringYourOwnKey`、`VolcengineBringYourOwnKey`。
- 祖名云端创建会话时服务端按权威时间校验：试用 3 天/600 秒，或 Pro 月桶/加量包。每会话最多预留 600 秒。
- 使用数据库事务或等价并发控制处理预留和结算；不得让两个并发会话透支同一额度。
- 成功按服务端实际收到的 PCM 样本/字节换算秒数结算；客户端提交的秒数只能用于诊断，不能用于扣费。
- 结算顺序固定：月度桶优先，加量包第二。取消、失败要释放预留，不减少额度。
- 建立客户端到服务端、服务端到百炼的 WebSocket 中转；不保存音频/识别正文；保留本地历史和本地音频。
- WPF 新增“订阅与额度”和账户菜单，但不得破坏既有首页/设置和悬浮胶囊。

### 测试

- 试用：3 天以内累计刚好 600 秒可用，601 秒拒绝；过期拒绝。
- Pro：36,000 秒月桶 + 36,000 秒加量包，实际消耗跨桶时月桶先归零。
- 会话取消、两次 ASR 失败、连接前失败均释放预留。
- 并发创建会话不能超额预留。
- 服务端日志和数据库中不出现音频帧或识别文本。

## 5. M3：自有模型、设置、帮助反馈

### 要实现

- 现有百炼自有 Key 必须继续使用 DPAPI；增加火山引擎真实适配器和独立连接测试，不以 Mock 冒充可用。
- 仅有效 Pro 开放自有 Key 表单和使用；过期则停用但保留加密配置，不删除用户 Key。
- 设置页分成：直接说/热键、麦克风、识别来源与高级 Key、系统与数据、写入兼容性。
- 不再保留用户不需要的“备用热键”产品入口；右 Alt 是唯一主热键，兼容问题保持自动回退与复制兜底的清晰提示。
- 添加 FAQ 和反馈邮件草稿。反馈只在用户主动同意时附版本、匿名设备 ID、诊断摘要；绝不附听写正文、音频、Key、Token。

### 测试

- Pro 到期前后自有 Key 的可用性准确切换。
- 使用自有模型前后祖名云端额度不变。
- 设置保存后重启保持；DB 不出现自有 Key 明文。
- FAQ 包含写入失败、360、额度、自有 Key、隐私说明。

## 6. M4：支付宝沙箱支付

### 要实现

- 服务端商品白名单：`pro_month`、`add_on_10h`。金额由服务端配置，禁止客户端传入/覆盖金额。
- 桌面端只调用创建订单 API 并以默认浏览器打开支付宝托管收银页。
- 服务端实现支付宝签名生成、通知验签、App ID/订单号/金额/交易状态校验、订单状态机和通知幂等。
- 支持成功、关闭、超时、重复通知、验签失败、退款状态。客户端回跳页和轮询只能展示结果，不能自己发权益。
- 写可重复执行的沙箱测试脚本给助理；测试资料只用沙箱账号与假数据。

### 测试

- 同一成功通知重复两次，Pro 天数或加量包只增加一次。
- 伪造签名、金额不一致、未知订单均不发权益。
- 取消订单不发权益；退款状态被记录且不自动错误扣回已使用额度。

## 7. M5：成本和发布准备（不得假定完成）

1. 用生产同规格百炼账号累计真实转写 10 小时。
2. 记录实际音频秒数、百炼账单、服务中转、数据库/日志、支付和支持成本。
3. 形成 `docs/COST-BENCHMARK-V0.7.md`，由负责人确认售价后才可把“20 元/月”从内测建议改为正式价格。
4. 微信 Native 支付必须等商户号、认证 AppID、证书和风控前置条件齐备后再做；V0.7 不能伪装“已支持微信支付”。

## 8. 每个里程碑的提交和交接格式

每个 M 阶段单独提交、推送，并在 PR/交接中给出：

1. 分支名和 commit SHA；
2. 改动文件列表和设计取舍；
3. `dotnet build`、`dotnet test`、数据库迁移、Docker 启动的真实输出；
4. 已做真人/沙箱验证与尚未做的真人验证，不能混淆；
5. 迁移/环境变量/密钥配置步骤；
6. 任何未完成项、风险和下一阶段建议。

完成全部 M1-M4 后，不要直接覆盖 `main` 或 v0.6.4。推送独立分支并通知审计方进行代码、安全、支付与真人回归审查。
