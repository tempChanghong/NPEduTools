# 手动录制命令回执恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：手动录制回执缺少状态或协议校验失败时，解除本次等待、保留旧会话，并通过状态轮询重新核实。** 修复前在模拟后台接收暂停请求，返回“成功”但不附录制状态，复现客户端保留“暂停中”和已确认标记，本次等待也没有解除。

| 情况 | 处理 |
| --- | --- |
| 回执缺少录制状态、请求编号或版本错误 | 提示本次操作未确认，当前状态待核实；保留最后确认的会话，解除本次等待 |
| 后台明确拒绝且携带状态 | 保留后台确认的状态，显示拒绝原因 |
| 正常成功并携带录制状态 | 显示确认后的状态 |
| 后续正常状态回执 | 恢复当前状态及操作能力，不重发原命令 |

状态未核实时继续使用原有操作限制，不能再提交录制命令。本轮仅补录制状态必需项检查和 InvalidDataException 的未确认处理；会话约束、Host 执行逻辑及实际录制流程保持原行为。

## 验证

- 修复前证据：.artifacts/manual-receipt-recovery/before/result.json，错误为 missing: invalid manual receipt retains a confirmed or optimistic recording state。
- 5 组新增隔离管道检查通过：缺少状态、错误请求编号、错误协议版本、明确拒绝和确认成功；各组验证会话约束及正常轮询核实，未知状态下不得再发第二条命令。
- 21 组已有回归通过：手动录课界面及恢复 5 组、录制轮询协议恢复 7 组、自动录课回执恢复 5 组、自动录课状态界面 4 组。
- 27/27 项协议、录制契约与执行策略测试通过，0 跳过；App 和界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.ProtocolTests|FullyQualifiedName~NPEduTools.Tests.RecordingTests|FullyQualifiedName~RecordingExecutionTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/manual-receipt-recovery/wpf" --manual-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/manual-receipt-recovery/automatic-recovery" --automatic-recovery
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/manual-receipt-recovery/automatic-status" --automatic-recording
node scripts/check-doc-links.mjs
```

新增检查使用唯一模拟管道、真实客户端及合成会话，没有启动真实录制组件、Host 或采集设备。真实录制、教室设备及生产验收未运行；本轮未推送、部署或发布。
