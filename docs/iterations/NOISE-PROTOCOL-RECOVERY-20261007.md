# 噪音监测协议恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：噪音监测页收到无效设备、状态或操作回执后清除旧读数，并由原有轮询恢复。** 修复前，模拟后台返回请求编号错误、版本错误、零长度帧或空消息：首次设备查询与后续状态轮询退出，页面保留旧读数；保存麦克风时，协议异常逃出操作处理。

本轮只将 InvalidDataException 纳入轮询和操作的现有失联处理。协议校验、设备枚举阶段和实例／修订校验保持不变；无效回执不更新设备列表。页面显示状态未知，清空实时电平、采样质量、统计与趋势，禁用采集及保存操作。操作解除等待，后续只读查询恢复当前数据与按钮，不自动重发设备选择或启动采集。

## 验证

- 修复前证据：.artifacts/noise-protocol-recovery/before/result.json，12 种路径／错误组合全部复现失败。
- 12 组新增隔离 WPF 检查通过：首次设备查询、后续状态查询、保存麦克风分别覆盖请求编号错误、版本错误、零长度帧和空消息；验证旧数据失效、无效设备列表不被应用、操作解除等待和只读查询恢复。
- 4 组已有状态展示回归通过，本次入口合计 16 组，覆盖已结束统计、采集中断、新后台无会话和最小窗口布局。
- 39/39 项采样、电平展示、模拟管道及协议测试通过，0 跳过。界面测试项目 Release 构建通过，0 警告、0 错误。
- 已查看最小窗口中未知统计和恢复后统计的截图，位于 .artifacts/noise-protocol-recovery/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NoiseTests|FullyQualifiedName~NoiseSignalPresentationTests|FullyQualifiedName~NoisePipeTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/noise-protocol-recovery/wpf" --noise-status
node scripts/check-doc-links.mjs
```

界面检查只连接唯一模拟管道，使用合成设备与统计；设备选择请求也只由模拟服务接收。没有运行真实 Host、枚举或采集真实麦克风、连接学校服务、启动 Guard 或修改真实管理设置。真实麦克风、教室设备及生产验收未运行；本轮未推送、部署或发布。
