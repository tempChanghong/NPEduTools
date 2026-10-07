# 学校互联状态提示 · 2026-10-07

[迭代索引](README.md) · [学校互联](../npep/README.md)

**交付：OOBE 与设置页使用同一套连接状态、中文原因和处理建议。** 改动前的回归记录确认：自动重试中的设备只显示“当前未在线”；未知连接状态也落入同一文案。失败证据保存在本机 `.artifacts/connection-presentation/before/before.trx`。

| 当前状态 | 展示与建议 |
| --- | --- |
| 已配对、连接中 | 等待新回执，不提前显示在线 |
| 暂时离线、后台会重试 | 等待自动重试，保留原配对／申请；不建议重复配对 |
| TLS 失败 | 核对学校地址、系统时间和服务器证书；仍保留每次证书验证 |
| 主动暂停 | 配对保留，由用户明确恢复 |
| 连接停止或学校停用 | 核对原因，按当前状态恢复或联系管理员；不声称会自动重连 |
| 后台不可用、未知状态或凭据不可用 | 不认定在线；刷新、核实后台或处理本机存储 |

`OFFLINE` 不是所有操作均自动恢复的证明：普通网络失败后的创建／确认等手动操作需要恢复原操作；已有配对、待审批申请和 TLS 重试按既有后台机制展示。界面没有精确重试时间字段，所以不新增倒计时。

原始状态、错误码、后台说明和可用操作编号放在默认折叠、可复制的技术详情。操作拒绝与当前状态错误分别保留；新状态到达后清除旧操作反馈。详情仅选择本机状态字段，不序列化服务／批准的 JSON 或配对凭据。

只修改 App 展示与本机反馈字段，不改 Host、服务协议、网络重试、配对流程、许可、按钮启用条件或 OOBE 完成条件。本轮未改网页或 KV；本机验证不包含推送、部署或发版。

## 验证

41 项展示／会话测试与 6 项既有后台回归通过（暂停保留、撤销停止、TLS 失败与恢复、恢复原配对候选）。隔离 WPF 测试使用真实共享控件及真实 OOBE 学校步骤，检查两处同步刷新、详情折叠／复制、旧错误清除、已有按钮及最小尺寸布局。App／界面测试项目 Release 构建通过，0 警告、0 错误。

文档检查覆盖 166 份 Markdown、800 个本地文件链接，`git diff --check` 通过；远程 URL、标题锚点及外部本机资料不在该链接检查范围。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NpepConnectionSessionTests|FullyQualifiedName~NpepConnectionPresentationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/connection-presentation/wpf" --school-connection
node scripts/check-doc-links.mjs
```

新 UI 参数仅运行本轮隔离检查，不启动生产 App／Host、不连接学校服务、不创建申请或改绑定。证据在 `.artifacts/connection-presentation/`，不随 Git 分发。真实大屏、实际学校网络、UAC 与生产验收均未运行；不能从本机自动检查推断这些环节通过。
