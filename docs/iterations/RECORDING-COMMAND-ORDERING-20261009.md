# 录制命令与轮询时序

[迭代索引](README.md) · [测试入口](../TESTING.md)

2026-10-09，基线 `bdc71af`。**交付：操作期间发起的旧查询，不能覆盖随后确认的录制结果。**

修复前，暂停录制或关闭自动录课已经收到成功回执，操作期间发起的状态查询仍可能晚到，将界面改回录制中或自动录课已启用；同一查询断线时则将已确认状态标为未知。客户端只在操作开始时更新查询批次，结束后仍接受期间发起的查询。

在手动录制与自动录课操作结束时再次更新批次，淘汰这些旧查询及其错误。继续正常轮询和租约续期；后续新查询仍可更新状态，不重发操作。Host、协议、采集和控制权限不变。

## 验证

- 暂停／关闭自动录课 × 旧查询迟到／断线：修复前四项均复现，记录在 `.artifacts/recording-command-order-20261009/repro/result.json`。
- 修复后手动录制界面检查 **28 项通过**，包含四项新回归；自动录课命令恢复 **5 项通过**。结果分别位于同目录的 `after/result.json` 与 `automatic-recovery/result.json`。
- 录制、录制进程与协议专项单元测试 **116 项通过，0 失败、0 跳过**，记录在同目录的 `tests/recording.trx`。界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
foreach ($mode in @('manual-recording', 'automatic-recovery')) {
    $check = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) ".artifacts/recording-command-order-20261009/$mode"), "--$mode") -WindowStyle Hidden -PassThru -Wait
    if ($check.ExitCode -ne 0) { throw "$mode 检查失败" }
}
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~Recording|FullyQualifiedName~RecorderProcess|FullyQualifiedName~ProtocolTests'
```

时序检查使用唯一模拟管道和真实界面客户端，先挂起查询，再返回操作回执，最后释放旧查询；确认状态保留、会话标识不变、新轮询恢复且不重放命令。不启动真实 Host 或采集，不连接学校服务。本轮未执行完整解决方案回归、真实设备或生产验收；提交保留本地，未推送、部署或发布。
