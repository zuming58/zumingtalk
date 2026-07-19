# V0.7 成本标定

## 当前状态

**待真实标定。** 代码、服务端权威用量汇总和采集脚本已完成，但尚未使用生产同规格百炼账号累计 36,000 秒真实音频。当前页面中的 20 元只允许作为内测建议价，不能标记为正式售价。

## 硬门槛

- 同一生产规格百炼账号累计真实收到音频不少于 36,000 秒。
- 服务端汇总不得包含音频、转写正文、录音路径、API Key 或设备令牌。
- 百炼账单、中转、数据库/日志、支付手续费和支持成本均有负责人提供的真实金额。
- `scripts/collect-cost-benchmark.ps1` 输出 `complete=true` 后，负责人才能填写最终结论。

## 采集命令

```powershell
.\scripts\collect-cost-benchmark.ps1 `
  -ServiceBaseUrl "https://your-test-service" `
  -AdminApiKey $env:ZUMINGTALK_ADMIN_API_KEY `
  -From "2026-07-20T00:00:00Z" `
  -To "2026-08-20T00:00:00Z" `
  -BailianBillFen 0 `
  -RelayCostFen 0 `
  -DatabaseAndLoggingCostFen 0 `
  -PaymentFeeFen 0 `
  -SupportCostFen 0
```

脚本读取 `/api/admin/cost/summary` 的实际 PCM 字节、音频秒数、结算秒数、成功/释放会话、支付通知和退款计数，并在本机 `artifacts/cost/` 生成 JSON。未满 36,000 秒时返回非零退出码。

## 负责人结论

| 项目 | 结果 |
| --- | --- |
| 实际音频秒数 | 待填写 |
| 百炼账单 | 待填写 |
| 中转成本 | 待填写 |
| 数据库与日志 | 待填写 |
| 支付手续费 | 待填写 |
| 支持成本 | 待填写 |
| 每音频小时综合成本 | 待填写 |
| 正式售价 | 未确认 |
| 负责人/日期 | 待填写 |

微信 Native 支付不属于 V0.7。未取得商户号、认证 AppID、证书和风控条件前，不得在产品或报告中声称支持微信支付。
