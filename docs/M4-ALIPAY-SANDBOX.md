# V0.7 M4 支付宝沙箱验证

## 已实现边界

- 商品仅允许 `pro_month` 和 `add_on_10h`，金额由服务端 `Alipay` 配置生成；客户端请求不含金额字段。
- 收银页参数使用 RSA2 签名，桌面端只打开支付宝托管 URL。
- 异步通知先验签，再核对 `app_id`、`seller_id`、订单号、金额和交易状态。
- 只有订单首次从 `PendingPayment` 进入 `Paid` 才发放 Pro 或加量包；不同通知 ID 和重复通知都不能重复发放。
- 关闭、30 分钟超时、退款均是服务端状态；回跳页不发权益。
- 全额退款由管理员接口调用支付宝 `alipay.trade.refund`。只有支付宝响应 RSA2 验签通过且 `fund_change=Y` 才记录 `Refunded`，不自动回滚已使用或已发放额度。

## 本机密钥配置

仅在本机环境变量或密钥管理中配置：

```text
ConnectionStrings__Zumingtalk
Service__AdminApiKey
Alipay__AppId
Alipay__SellerId
Alipay__MerchantPrivateKeyPath
Alipay__AlipayPublicKeyPath
Alipay__NotifyUrl
Alipay__ReturnUrl
```

私钥、公钥、沙箱账号、设备令牌和数据库密码不得提交。`NotifyUrl` 必须是支付宝沙箱可访问的 HTTPS 地址；数据库需执行 `001_initial_commerce.sql` 和 `002_alipay_payments.sql`。

## 可重复沙箱流程

```powershell
.\scripts\alipay-sandbox-smoke.ps1 `
  -ServiceBaseUrl "https://your-test-service" `
  -DeviceToken $env:ZUMINGTALK_DEVICE_TOKEN `
  -ProductId pro_month
```

脚本创建订单、打开托管收银页并轮询服务端状态。取消订单、重复通知、退款和伪造回调由服务端自动测试覆盖；真实支付宝沙箱验签与退款仍必须在最终统一人工验收时执行，不能用测试 RSA 密钥冒充沙箱证据。
