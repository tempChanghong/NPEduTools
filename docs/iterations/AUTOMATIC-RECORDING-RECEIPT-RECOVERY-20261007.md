# 自动录课命令回执恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：自动录课命令结果未确认时，立即提示当前状态未知，由正常轮询核对结果。** 修复前用唯一模拟管道接收关闭命令后断开，保留此前状态查询等待返回，复现界面继续显示旧的已确认录制状态。

| 回执情况 | 处理 |
| --- | --- |
| 连接断开、超时、协议不匹配或缺少自动录课状态 | 通知界面当前状态未知，操作提示说明“自动录课操作未确认”；保留旧会话及自动录课快照供核对 |
| 后台明确拒绝且携带状态 | 展示后台实际状态和拒绝原因 |
| 正常成功且携带状态 | 更新实际状态，清除本次操作反馈 |
| 后续正常轮询 | 恢复实际状态及对应总开关显示；本次操作提示作为上次操作信息保留 |

没有自动重发开关或跳过命令。原录制器截止期限、租约、Host 执行逻辑、试运行和计划文件格式保持原行为。回执不确定时同步使手动录制状态待核实，避免用旧会话显示已确认的实时运行信息。

## 验证

- 修复前失败证据：`.artifacts/automatic-recovery/before/result.json`，错误为 `dropped: uncertain automatic receipt leaves a confirmed live status`。
- 5 组隔离管道和 WPF 控件检查通过：丢失回执、缺少自动状态、请求编号不匹配、明确拒绝、确认成功；每组再验证正常轮询恢复、旧查询不能短暂覆盖新状态、命令不自动重发及试运行独立。
- 12 组已有界面回归通过：自动录课状态 4 组、手动录课恢复 5 组、计划保存反馈 3 组。
- 18/18 项 `RecordingTests` 和 `RecordingExecutionTests` 通过，0 跳过；App 和界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.RecordingTests|FullyQualifiedName~RecordingExecutionTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/automatic-recovery/wpf" --automatic-recovery
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/automatic-recovery/automatic-regression" --automatic-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/automatic-recovery/manual-regression" --manual-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/automatic-recovery/plan-save-regression" --recording-plan-save
node scripts/check-doc-links.mjs
```

管道检查使用真实客户端请求和模拟后台，窗口控件使用合成状态，隔离计划文件结束后清理。本轮没有启动真实 Host、录制组件或采集设备，真实录制、教室设备及生产验收未运行；未推送、部署或发布。
