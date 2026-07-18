# V0.7 M1 商业化基础运行说明

M1 新增的商业数据只存在 PostgreSQL。WPF 客户端 SQLite 继续只保存本地听写历史、录音、界面缓存和 DPAPI 加密的用户自有 Key，不能作为邀请码、权益或额度的权威来源。

## 本地 PostgreSQL

1. 复制 `.env.example` 为被 Git 忽略的 `.env`，为 `POSTGRES_PASSWORD` 和 `ZUMINGTALK_ADMIN_API_KEY` 设置仅本机使用的随机值。
2. 运行 `docker compose up -d`。容器首次创建数据库时会执行 `src/Zumingtalk.Service/Data/Migrations/001_initial_commerce.sql`。
3. 使用本机环境变量配置服务，示例：

```powershell
$env:ConnectionStrings__Zumingtalk = 'Host=127.0.0.1;Port=5432;Database=zumingtalk;Username=zumingtalk_dev;Password=<local-password>'
$env:Service__AdminApiKey = '<local-admin-key>'
dotnet run --project src/Zumingtalk.Service
```

服务不会在 `appsettings.json`、日志、异常详情或 Git 中保存数据库密码、管理员密钥、邀请码明文或设备令牌明文。

## M1 接口验证

- `POST /api/admin/invites` 需要 `X-Admin-Key`，响应中的邀请码只显示这一次；数据库仅保存 SHA-256 哈希。
- `POST /api/activation` 接收邀请码和匿名设备指纹，响应中的 Bearer 设备令牌只显示这一次；数据库仅保存哈希。
- `GET /api/me/entitlement` 需要 `Authorization: Bearer <device-token>`。
- `POST /api/admin/devices/{id}/reset` 吊销旧令牌并允许邀请码被重新绑定。
- `POST /api/admin/devices/{id}/pro` 创建 30 天 Pro 权益和 36,000 秒月度桶；`add-on` 创建不失效的 36,000 秒加量桶。

管理员请求可设置 `X-Admin-Actor` 作为审计操作者标识。审计只记录操作类型、目标 ID 和固定业务元数据，绝不记录邀请码、令牌、音频、转写正文或密钥。

## 当前限制

- 本机需要 Docker Desktop 才能执行 PostgreSQL 初始化验证；M1 的 API 自动化测试使用独立 SQLite 测试数据库验证 HTTP、鉴权和业务约束，不将其当作 PostgreSQL 迁移验收。
- M2 才会实现 PostgreSQL 额度预留、事务结算和百炼 WebSocket 中转。M1 的额度桶仅由激活和管理员操作创建。
