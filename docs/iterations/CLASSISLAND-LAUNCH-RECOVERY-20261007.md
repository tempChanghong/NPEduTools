# ClassIsland 启动回执恢复 · 2026-10-07

[迭代索引](README.md) · [使用指南](../GETTING-STARTED.md)

**交付：启动结果未确认时清除旧管理员状态，后台停止后不再处理迟到回执并继续下一步。** 修复前在真实主窗口启动流程中注入替身，复现两处问题：启动回执丢失仍显示旧管理员运行和插件状态；应用生命周期已停止后，迟到的“需要授权”回执仍触发第二次管理员操作。

| 情况 | 页面行为 |
| --- | --- |
| 启动组件异常或最终回执没有状态、且非取消 | 任务、运行权限及插件状态待核实，清除旧指纹；可重新检查，不自动重发启动 |
| 取消管理员重启授权 | 保留原已知进程状态和手动重试入口 |
| 已确认进程，但课程验证回执丢失 | 保留已确认权限与验证请求编号，只查询该请求的结果；未确定前不允许再次启动 |
| 后台停止或窗口关闭后回执才返回 | 忽略回执，不继续发起第二次授权或课程验证；已经发出的操作需另行核实 |

仅修改 App 启动入口的状态失效和生命周期检查。正常按钮仍使用原管理员客户端；权限策略、UAC、启动方式、课程就绪条件、Host 去重和 Windows 自启动规则不变。内部入口允许注入操作替身。

## 验证

- 修复前失败证据：`.artifacts/classisland-launch/before/result.json`、`stopped-before/result.json`。
- 7 组隔离 WPF 检查通过：生命周期停止后“需要授权”及“成功”回执、启动未知、组件异常、第二阶段管理员回执未知、取消重启、课程验证回执丢失后按原请求恢复。
- 5 组管理员状态、6 组配置请求界面回归重新运行通过。
- 42/42 项 `LaunchTests` 和 `AdminStartupTests` 替身／纯策略回归通过，0 跳过；App 与界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.LaunchTests|FullyQualifiedName~AdminStartupTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/classisland-launch/wpf" --classisland-launch
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/classisland-launch/`，不随 Git 分发。管理员操作全部由替身提供合成结果；连接验证与结果查询只发送至唯一模拟 Host 管道。未启动真实管理员组件、UAC、ClassIsland 或 Host，没有改变 Windows 计划任务。真实后台停止、设备启动、班级大屏和生产验收未运行；本轮未推送、部署或发布。
