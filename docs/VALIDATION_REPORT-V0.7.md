# 祖名闪电说 V0.7 商业化开发验收报告

日期：2026-07-19

## 结论

M1-M5 的代码、自动化测试、迁移、运行脚本和 Windows 发布候选已完成。当前产物适合交给审计方和用户进行统一真实环境验收，但不是正式商业发布：真实 PostgreSQL、百炼/火山 ASR、支付宝沙箱、跨应用写入、360 和累计 10 小时成本标定尚未执行。

## 阶段结果

| 阶段 | 开发结果 | 自动证据 | 仍需真实测试 |
| --- | --- | --- | --- |
| M1 | PostgreSQL 商业数据层、邀请码单设备激活、哈希令牌、管理员 Pro/加量/重置与审计 | 邀请码一次性、令牌吊销、固定权益和敏感信息测试通过 | Docker PostgreSQL 迁移和真实服务部署 |
| M2 | 事务额度预留/结算、月桶优先、WSS 百炼中转、桌面激活和额度页 | 并发边界、失败释放、幂等结算和无正文账本测试通过 | 真实服务端百炼、麦克风和断网 |
| M3 | 百炼/火山自有 Key、DPAPI、Pro 门禁、设置分区、FAQ/反馈 | 火山协议帧、门禁、设置持久化、非明文凭证测试通过 | 真实百炼/火山连接与额度不变验证 |
| M4 | 支付宝沙箱下单、RSA2、异步通知、金额核对、幂等权益、关闭/超时/退款 | 伪造签名、金额篡改、未知订单、重复通知、退款验签等测试通过 | 真实沙箱支付、回调、取消和退款 |
| M5 | 无正文成本汇总、36,000 秒硬门槛、发布脚本、0.7.0 自包含包 | 成本字段安全测试、版本/hash/ZIP 内容和启动烟测通过 | 累计真实 10 小时并由负责人确认售价 |

## 自动验证

```powershell
dotnet build Zumingtalk.sln -c Release --no-incremental
dotnet test Zumingtalk.sln -c Release --no-build
dotnet list Zumingtalk.sln package --vulnerable --include-transitive
.\scripts\publish-win-x64.ps1
```

- Release 构建：`0 warning / 0 error`。
- 测试：`70 passed / 0 failed`，其中桌面/基础 56 项、服务端 14 项。
- NuGet：全部 7 个项目未发现当前源已知易受攻击包。
- PowerShell：支付沙箱、成本采集和发布脚本语法解析通过。
- EF Core：实体表/列使用 snake_case，与 PostgreSQL 迁移一致。
- ZIP：493 个条目，包含 EXE；敏感扩展名条目为 0。
- 启动烟测：发布 EXE 启动后持续存活 3 秒，再由测试进程结束。

## 发布候选

- 版本：`ProductVersion 0.7.0`，`FileVersion 0.7.0.0`。
- 目录：`artifacts/publish/v0.7.0-win-x64`
- ZIP：`artifacts/publish/Zumingtalk-v0.7.0-win-x64.zip`
- EXE SHA-256：`1988729E29F69996C30FA2BE113380C255179A9CFB2CF846DBABEEE4B6D79400`
- ZIP SHA-256：`03994A3ABD0134FB4043098D5386C7CB7C39967B41418BCA01C5F568821B02E1`

## 环境与迁移

服务端必须通过环境变量或本机密钥管理配置 PostgreSQL、管理员 Key、服务端百炼 Key、支付宝 App ID/卖家 ID、商户私钥路径、支付宝公钥路径、通知 URL 和回跳 URL。按顺序执行：

1. `src/Zumingtalk.Service/Data/Migrations/001_initial_commerce.sql`
2. `src/Zumingtalk.Service/Data/Migrations/002_alipay_payments.sql`

密钥、Token、录音、数据库、日志和 `artifacts/` 均未进入 Git。

## 统一人工验收

1. Docker PostgreSQL 执行两份迁移，启动服务并完成邀请码激活、令牌吊销和并发额度验证。
2. 真实服务端百炼与真实自有百炼/火山分别完成连接、听写、失败重试和录音保留。
3. 记事本、浏览器、VS Code、Codex、微信在 360 开/关两组环境中验证右 Alt、胶囊、自动写入、历史和复制兜底。
4. 支付宝沙箱完成成功、取消、重复通知、伪造签名、金额不一致和退款。
5. 使用生产同规格百炼累计 36,000 秒，运行成本脚本，负责人确认正式售价。

以上通过后才可进入审计结论、正式定价、GitHub Release 或合并流程。V0.7 不支持微信支付，也不允许在前置条件不齐时声称支持。
