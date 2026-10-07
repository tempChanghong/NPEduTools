# 自动录课计划保存反馈 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：计划已经保存、试运行记录保存失败时，分别说明结果，不再用普通成功提示覆盖错误。** 修复前在真实自动录课窗口使用隔离课表开始试运行，锁定试运行记录文件，再停用规则，复现“试运行已停止，页面仍显示规则已保存”。

| 保存结果 | 页面行为 |
| --- | --- |
| 计划文件写入失败 | 提示未应用修改，保留原计划；解除文件锁后可重试 |
| 计划已保存，随后试运行记录写入失败 | 明确计划已保存、试运行记录无法保存、试运行已停止；保留已提交的计划和磁盘上的旧记录 |
| 保存正常 | 显示该操作的成功提示 |

继续沿用原有保存失败处理：停用试运行及修改，实际录课状态和总开关不因这次试运行记录失败而改变。重新打开窗口后读取已保存的计划；文件恢复可写后可以继续保存，试运行不会自动恢复。没有改动 Host 录制执行、计划格式或实际录制策略。

## 验证

- 修复前失败证据：`.artifacts/recording-plan-save/before/result.json`，错误为 `partial save is masked by generic success: 规则已保存`。
- 3 组隔离 WPF 检查通过：计划写入失败及解锁重试；计划已提交但试运行记录写入失败；重开窗口读取计划及恢复保存。
- 4 组已有自动录课状态界面回归通过；67/67 项 `CalendarRecordingPlannerTests`、`RecordingPlannerTests`、`SchoolClockTests` 通过，0 跳过。
- App 和界面测试项目 Release 构建通过，0 警告、0 错误；已查看失败反馈界面截图。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~CalendarRecordingPlannerTests|FullyQualifiedName~RecordingPlannerTests|FullyQualifiedName~SchoolClockTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-plan-save/wpf" --recording-plan-save
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-plan-save/automatic-regression" --automatic-recording
node scripts/check-doc-links.mjs
```

界面检查使用唯一测试端点对应的本机临时配置和真实文件锁，测试结束清理自己的文件；实际录课状态、课表和学校时间使用合成数据。没有连接真实 Host、启动录制、采集设备、修改实际配置或操作生产。真实录课、教室设备和生产验收未运行；本轮未推送、部署或发布。
