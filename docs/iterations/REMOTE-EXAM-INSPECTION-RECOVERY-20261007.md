# 远程考试检查失败后的状态恢复 · 2026-10-07

[迭代索引](README.md) · [考试模式](../GETTING-STARTED.md)

**交付：考试检查失败后，当前状态明确变为未知，不再同时展示旧绑定与旧暂停事实。**

修复前，用真实按钮事件、HostClient 与唯一模拟管道复现 10 组失败：环境检查／当前暂停核实分别收到请求标识错误、版本错误、零长度帧、JSON 空消息或断连。页面显示“检查结果未读取”，但仍保留旧运行状态、学校绑定、暂停详情和历史展示。

生产代码仅在既有检查异常分支调用 ApplyRemoteExam(null)。复用现有未知状态展示与检查资格清理，不增加请求或更改考试控制协议。

| 阶段 | 页面行为 |
| --- | --- |
| 等待检查 | 检查与解除暂停按钮不可用 |
| 检查失败 | 提示未读取；清除旧绑定、暂停详情和历史展示；检查按钮恢复，解除暂停不可用 |
| 有效只读查询恢复 | 显示新实例许可和新暂停记录；原检查资格不复用 |
| 用户重新核实 | 核实当前修订与控制标识后恢复解除暂停资格 |

这里清除的是页面缓存，未删除本机历史记录，也未解除实际录课暂停。

## 验证

- 修复前：10 组隔离事件检查均失败，见本机 .artifacts/remote-inspection-recovery/before/result.json。
- 修复后：12 组 WPF 检查通过，包括上述 10 组恢复与 2 组既有历史模板检查。验证真实 async-void 事件完成、错误提示、旧状态失效、按钮恢复、新状态查询以及明确重新核实。
- RemoteExamTests、RemoteExamPresentationTests、ProtocolTests：88/88 通过，0 跳过。
- App 与界面测试 Release 构建通过：0 警告、0 错误。已查看未知状态截图。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~RemoteExamTests|FullyQualifiedName~RemoteExamPresentationTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/remote-inspection-recovery/wpf" --remote-exam
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。只初始化 MainWindow 的 XAML 与事件，不调用 Start；请求仅限模拟管道上的 remoteexam.preflight、remoteexam.inspect、remoteexam.status。未启动真实 Host、学校连接、录制、守护或教学软件；未发出解除暂停、软件切换或自启动命令。实际大屏和生产验收未运行。
