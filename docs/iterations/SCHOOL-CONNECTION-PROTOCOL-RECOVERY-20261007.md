# 学校互联协议异常恢复 · 2026-10-07

[迭代索引](README.md) · [学校互联](../GETTING-STARTED.md)

**交付：无效后台回执不再逃出学校互联共享会话；清除旧连接状态、解除等待，允许只读查询恢复。**

修复前，实际 HostClient 和唯一模拟管道复现 12 组失败：状态查询、创建配对申请、暂停互联，分别收到请求标识不匹配、协议版本不匹配、零长度帧或 JSON 空消息。InvalidDataException 逃出 RunAsync，旧状态未清除。设置与 OOBE 的共享控件检查也因同类异常失败。

生产代码仅补齐 NpepConnectionSession.RunAsync 的异常过滤，复用既有未知状态处理：

| 情况 | 结果 |
| --- | --- |
| 状态查询收到无效回执 | 连接显示未确认，旧配对／在线事实失效，刷新仍可用 |
| 配对或暂停操作收到无效回执 | 提示操作可能已受理；在取得新状态前禁止再次提交 |
| 下次取得有效状态 | 显示实际待批准申请或已暂停状态；不重新创建申请或重复暂停 |
| 本地正在编辑设备信息 | 保留地址、设备名称和输入的配对码；清除服务／归属确认勾选 |

设置与 OOBE 共用同一会话，均停止使用旧在线状态和完成资格。未修改服务端配对协议、授权流程、证书验证或轮询间隔。

## 验证

- 修复前：12/12 管道案例失败，见本机 .artifacts/school-protocol/before.log；共享界面失败见 before/result.json。
- 修复后：NpepConnectionProtocolTests、NpepConnectionSessionTests、NpepConnectionPresentationTests、ProtocolTests 合计 62/62 通过，0 跳过。
- 管道案例验证未知状态、门闩释放、本地输入保留、确认重置、只读恢复、新请求标识，以及已创建申请／已暂停状态的正确显示。
- 4 组隔离 WPF 检查通过，包括两份真实共享控件和实际 OOBE 的 720×600 学校步骤。已查看未确认状态截图。
- App 与 WPF 测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NpepConnectionProtocolTests|FullyQualifiedName~NpepConnectionSessionTests|FullyQualifiedName~NpepConnectionPresentationTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/school-protocol/wpf" --school-connection
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。管道测试只连接唯一模拟 Host，使用合成服务标识与配对码；WPF 控件检查注入模拟状态与异常，只发只读查询。未启动真实 Host、学校连接、麦克风、管理验证或守护；实际服务器批准、设备配对和生产验收未运行。
