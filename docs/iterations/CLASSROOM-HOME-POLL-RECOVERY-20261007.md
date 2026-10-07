# 主页课堂模式轮询恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：主页收到无效课堂状态回执后显示“状态未知”，继续查询并在有效回执到达后恢复。** 修复前，模拟后台返回不匹配的请求编号，独立课堂轮询抛出 InvalidDataException 并退出，主页仍保留此前确认的考试模式和录课暂停信息。

本轮只将该协议异常纳入现有查询失败处理。保留请求编号及协议版本校验、查询超时和轮询间隔；失败时使用已有未知状态展示，不推测切换成功或解除暂停。该查询只读取状态，不修改课堂模式、自启动或录课设置。

## 验证

- 修复前证据：.artifacts/classroom-home-poll/before/result.json，真实主页轮询因请求编号不匹配而退出。
- 6 组新增隔离 WPF 检查通过：请求编号错误、版本错误、零长度帧、空消息、缺失模式快照、等待时取消。前五组确认旧模式／暂停显示失效，同一个轮询在有效日常模式回执后恢复；取消时正常退出。
- 5 组已有课堂模式界面检查通过，覆盖查询顺序、过期配置检查、断连恢复、缺失快照和配置检查被新查询取代。
- 44/44 项课堂模式及配置检查测试通过，0 跳过。界面测试项目 Release 构建通过，0 警告、0 错误。
- 主页未知状态截图与修复后结果位于 .artifacts/classroom-home-poll/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ClassroomModeTests|FullyQualifiedName~ClassroomRuntimeTests|FullyQualifiedName~ClassroomSetupTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/classroom-home-poll/wpf" --classroom-status
node scripts/check-doc-links.mjs
```

使用唯一模拟管道与隔离主页，不初始化真实后台、录制或系统操作服务。真实模式切换、教室设备和生产验收未运行；本轮未推送、部署或发布。
