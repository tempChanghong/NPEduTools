# 课堂模式管理页协议恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：课堂模式管理页收到无效回执后，使旧模式与恢复目标失效，并继续只读查询恢复。** 修复前，唯一模拟后台分别返回请求编号错误、版本错误、零长度帧或空消息：状态轮询退出并保留旧模式；手动核实则让协议异常逃出操作处理。

本轮只将 InvalidDataException 纳入轮询和操作的现有失联处理。保留协议校验、修订核对与查询时序规则；异常时模式、录课暂停、实际自启动设置和恢复目标显示未知，清除配置检查结果，禁用切换与恢复按钮。操作解除等待并提示核实，不自动重发。有效查询恢复相应控制；失效的配置检查仍需重新检查。过期查询的错误不能覆盖较新的核实结果。

## 验证

- 修复前证据：.artifacts/classroom-protocol-recovery/before/result.json，8 种路径／错误组合全部复现失败。
- 8 组新增隔离 WPF 检查通过：状态轮询与手动核实分别覆盖请求编号错误、版本错误、零长度帧和空消息；验证旧事实及配置检查失效、解除等待、按钮禁用和日常状态恢复。
- 另补 1 组时序回归：新核实成功后，迟到的旧无效查询不清除新模式；随后有效快照仍可显示未完成切换和恢复入口。
- 11 组已有主页／管理页回归通过，本次入口合计 20 组。53/53 项课堂模式、配置检查及协议测试通过，0 跳过。
- 界面测试项目 Release 构建通过，0 警告、0 错误。已查看最小窗口中未知模式、未知恢复记录和恢复后的截图，位于 .artifacts/classroom-protocol-recovery/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ClassroomModeTests|FullyQualifiedName~ClassroomRuntimeTests|FullyQualifiedName~ClassroomSetupTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/classroom-protocol-recovery/wpf" --classroom-status
node scripts/check-doc-links.mjs
```

界面检查仅发送 classroom.status 和 classroom.refresh，使用唯一模拟管道与合成配置检查，不调用真实管理员工具、授权对话框或教学软件，不修改 Windows 自启动和录课状态。真实切换、教室设备及生产验收未运行；本轮未推送、部署或发布。
