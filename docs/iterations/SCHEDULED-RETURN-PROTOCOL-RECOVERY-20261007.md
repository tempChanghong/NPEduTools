# 定时监测返回的协议异常恢复 · 2026-10-07

[迭代索引](README.md) · [既有返回回执隔离](SCHEDULED-DISPLAY-RETURN-20261007.md)

**交付：返回作业板收到无效协议回执时，保留监测页面、显示未确认提示，并允许手动重试。**

修复前，用唯一模拟管道复现 12 组失败：请求标识不匹配、版本不匹配、零长度帧和 JSON 空消息，分别覆盖同一会话、等待期间切换会话、等待期间切换后台实例。InvalidDataException 逃出返回任务；同一任务也被按钮事件和关闭窗口入口调用。

生产代码仅补齐 ScheduledNoiseWindow.ReturnAsync 的异常过滤。已有会话标识检查继续隔离旧回执；既有等待门闩和 finally 继续恢复返回按钮。不会自动重发返回请求，不把未知结果当成成功。

| 情况 | 结果 |
| --- | --- |
| 同一会话收到无效回执 | 页面保持可见，提示后台尚未确认返回期限，按钮恢复 |
| 等待期间切换会话／后台实例 | 保留新读数、返回分钟数和消息，忽略旧错误 |
| 用户再次返回 | 新请求标识和当前实例／会话标识；有效成功回执后隐藏页面 |
| 请求尚未完成时重复返回 | 本地忽略重复调用 |

## 验证

- 修复前：12 组协议异常均失败，见本机 .artifacts/scheduled-return-protocol/before/result.json。
- 修复后：19 组隔离 WPF 检查通过，包含 12 组协议异常和 7 组既有成功／拒绝／断连检查；失败和会话切换后均核对显式重试及新请求标识。
- NoiseDisplayTests、NoiseScheduleHostTests、ProtocolTests：59/59 通过，0 跳过。
- App 与界面测试项目 Release 构建：0 警告、0 错误。
- 已查看合成未确认提示截图；窗口保留，返回按钮可用。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NoiseDisplayTests|FullyQualifiedName~NoiseScheduleHostTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/scheduled-return-protocol/wpf" --scheduled-display
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。检查只向唯一模拟管道发送 noise.display.return，使用合成读数，不启动真实 Host、麦克风、守护或学校连接。通过结果仅证明桌面错误处理与模拟回执闭环；真实网页返回期限、班级大屏、全屏多显示器及生产验收未运行。
