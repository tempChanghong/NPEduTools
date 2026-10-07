# 定时监测备用展示的返回回执 · 2026-10-07

[迭代索引](README.md) · [噪音使用指南](../GETTING-STARTED.md#噪音监测)

**交付：旧监测会话的返回回执不会隐藏新会话的原生备用页面。** 修复前，唯一模拟 Host 管道已复现：发起“返回作业板”，在请求等待期间更新为新会话，再返回旧请求的成功回执，新页面被错误隐藏。

| 情况 | 页面行为 |
| --- | --- |
| 返回请求等待中 | 暂停再次返回；继续接收当前展示与读数 |
| 等待期间后台实例或监测会话已改变 | 忽略旧请求的成功、拒绝或连接错误；保留新页面与消息，恢复返回按钮 |
| 新会话再次点击返回 | 请求携带新实例与会话标识；成功后正常隐藏页面 |
| 同一会话内更新读数或返回分钟数 | 原请求仍有效；成功正常返回，拒绝或断连显示原有提示并允许重试 |

仅修改 App 的 `ScheduledNoiseWindow.ReturnAsync`，在应用回执前核对当前实例与会话。Host、学校接口、网页、限时返回期限、重复请求规则及监测保护不变；不自动重发返回，不停止或重新开始采集。

## 验证

- 修复前失败：`.artifacts/scheduled-display/before/result.json`，记录旧成功回执隐藏新页面。
- 7 组隔离 WPF 检查通过：新会话成功／拒绝／断连、新 Host 成功，以及同一会话的成功／拒绝／断连。新会话各场景继续核对下一次返回使用新标识并正常隐藏；等待中重复调用不会再次发送请求。
- 已查看 1100×780 的合成新会话截图，读数、返回分钟数、消息和按钮完整显示。未验证实际全屏、多显示器或高 DPI 大屏布局。
- 50/50 项既有 `NoiseDisplayTests` 与 `NoiseScheduleHostTests` 回归通过，0 跳过。App 与界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NoiseDisplayTests|FullyQualifiedName~NoiseScheduleHostTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/scheduled-display/wpf" --scheduled-display
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/scheduled-display/`，不随 Git 分发。新增界面检查使用真实窗口、客户端和协议，但仅向唯一模拟管道发送 `noise.display.return`，没有启动 MainWindow、真实 Host、麦克风或学校连接。合成返回成功不代表真实网页或学校已收到返回请求。班级大屏和生产验收未运行；本轮不含推送、部署或发布。
