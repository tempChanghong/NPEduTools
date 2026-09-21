# NPEP N1 协议交付

本目录用于 NPEduTools 与 NPClassworks / NPClassworksKV 双端协作。N1 仅包含配对、撤销与只读状态，唯一能力为 `device.status`；通知和考试模式属于后续阶段。

2026-09-21：已完成 [Docker 与 N1 CI 接入](NPEP-N1-DEPLOYMENT-CI-REPORT.md)，并取得三个功能分支的 [GitHub 托管检查通过记录](NPEP-N1-HOSTED-CI-REPORT.md)。功能分支尚未合入会自动部署的 main；生产发布审核与 [真实大屏现场验收](NPEP-N1-FIELD-ACCEPTANCE.md) 仍待分别完成。

2026-09-20 双端交叉审阅完成：已提出的契约阻塞均闭环，可以作为隔离环境实现基线。服务端审阅记录的正文快照 SHA-256 为 `1afd58c435e6616a6dcd5091c0432f700b2e3325fdad2b997e089dc361490d07`；随后仅将主文档首段状态由“正在审阅”更新为“审阅通过”，未变更协议内容。

| 文件 | 用途 |
| --- | --- |
| [NPEP-N1-CONTRACT.md](NPEP-N1-CONTRACT.md) | 流程、身份、HTTP 路由、代际、错误码、事务和验收要求；业务语义的主文档 |
| [n1-wire.schema.json](n1-wire.schema.json) | JSON Schema Draft-07 定义库；按 `#/definitions/<名称>` 选择请求体或响应定义，根本身不是单一路由校验器 |
| [n1-examples.json](n1-examples.json) | 21 个有效结构与 20 个无效结构，全部为假数据 |
| [Test-N1Examples.ps1](Test-N1Examples.ps1) | 对全部示例检查预期通过/拒绝，不启动服务、不访问数据库 |
| [NPEP-N1-DEVICE-GUIDE.md](NPEP-N1-DEVICE-GUIDE.md) | 设备适配器、独立 CLI、凭据与故障恢复、隔离联调方法 |
| [NPEP-N1-IMPLEMENTATION-REPORT.md](NPEP-N1-IMPLEMENTATION-REPORT.md) | 实现范围、实际测试结果和下一阶段限制 |
| [NPEP-N1-UI-REPORT.md](NPEP-N1-UI-REPORT.md) | 设置页、Host 常驻连接与双端 UI 阶段的验收及限制 |
| [NPEP-N1-RESILIENCE-REPORT.md](NPEP-N1-RESILIENCE-REPORT.md) | 真实服务断线、丢回执、独立进程正常退出及突然终止恢复 |
| [NPEP-N1-FIELD-ACCEPTANCE.md](NPEP-N1-FIELD-ACCEPTANCE.md) | 已知域名、main 自动发布边界、上线前置条件及现场待填表 |
| [NPEP-N1-DEPLOYMENT-CI-REPORT.md](NPEP-N1-DEPLOYMENT-CI-REPORT.md) | Windows 检查、Docker/恢复接入、跨端 CI 门槛及未发布边界 |
| [NPEP-N1-HOSTED-CI-REPORT.md](NPEP-N1-HOSTED-CI-REPORT.md) | 三端 GitHub 实际结果、固定提交组合、checkout 修复与测试产物 |
| [服务端审阅记录](../../../NPClassworksKV/docs/NPEP-N1-CONTRACT-REVIEW.md) | 对配对响应丢失、并发撤销、绑定生命周期、状态乱序及恢复流程的交叉审查 |

## 验证方式

在 PowerShell 7.5 或更新版本执行：

```powershell
./docs/npep/Test-N1Examples.ps1
```

2026-09-20 本地结果：**41/41 符合预期**。其中包括未来控制能力、额外执行字段、秘密泄漏到响应、缺失部署标识、序号越界/错误类型、陈旧采样年龄、隐私字段、错误枚举、协议版本、时间格式等反例。

验证器保留 JSON 时间字符串，避免 PowerShell 自动转换日期后丢失协议要求的毫秒格式。该脚本不替代两端的真实请求解析器；重复 JSON 键、身份鉴别、速率限制、数据库锁、幂等执行、DPAPI 和 TLS 等行为需要在 N1 实现后单独验证。

## 实现分工与顺序

1. KV 负责人：从主契约建立独立 NPEP 数据模型、学校/账号/绑定生命周期锁定与迁移设计，先在隔离数据库实现 info / pairing / confirm / me / session / status / revoke；网页只开放学校管理员的只读设备登记流程。
2. NPEduTools 负责人：核实最新主分支与发布修订后，在独立开发分支实现协议模型、受保护凭据存储、单实例会话及只读状态适配；首先用假服务端做网络/丢回执测试，不操作真实模式、录制或 ClassIsland 提醒。
3. 两端联调：使用测试学校、两个学校的账号、测试 screen binding 和独立本地配置；先验证“配对成功—报告状态—撤销失效”，再执行主文档第8节竞态/恢复用例。
4. 现场配对界面与学校管理页完成后，才交付一个可用的 N1 试点。不把当前结构检查结果标为服务端或大屏安全验收通过。

2026-09-20 已进入隔离实现阶段：设备端原型和首个真实 HTTPS/PostgreSQL 闭环已完成；KV 的迁移、生命周期与部署 epoch 门禁由服务端分支实现并单独验收。不得沿用旧版本“无迁移”结论，也不得直接改线上服务试验。遇到契约变更，先更新主文档、Schema、正反例和双端审查，再一起实现；不得由任一端悄悄扩大能力。
