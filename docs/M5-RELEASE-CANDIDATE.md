# V0.7 M5 发布候选说明

- 桌面版本：`0.7.0 / 0.7.0.0`。
- 目标：Windows 11 x64，自包含 .NET 10 WPF。
- 本包是统一人工验收候选，不是正式商业发布；真实 ASR、支付宝沙箱、跨应用写入、360 和 10 小时成本标定通过前不得合并 `main` 或对外宣称正式上线。

生成命令：

```powershell
.\scripts\publish-win-x64.ps1
```

脚本拒绝复用非空目录，避免旧文件混入；输出 EXE、ZIP 和 SHA-256。发布产物位于 `artifacts/`，不进入 Git。

本次候选包：

- 目录：`artifacts/publish/v0.7.0-win-x64`
- ZIP：`artifacts/publish/Zumingtalk-v0.7.0-win-x64.zip`
- EXE SHA-256：`1988729E29F69996C30FA2BE113380C255179A9CFB2CF846DBABEEE4B6D79400`
- ZIP SHA-256：`03994A3ABD0134FB4043098D5386C7CB7C39967B41418BCA01C5F568821B02E1`
