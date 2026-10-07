# 本机课堂模式状态恢复 · 2026-10-07

[迭代索引](README.md) · [软件配置指南](../GETTING-STARTED.md#关联教学软件)

**交付：本机课堂模式页在失联后显示未知，旧查询与过期配置检查不再覆盖新结果。** 修复前分别复现：后台断连后仍显示旧考试模式与录课暂停；较早查询覆盖新只读核实结果；断连前发起的配置检查在断连后重新显示“四项已就绪”。

| 情况 | 页面行为 |
| --- | --- |
| 尚未确认后台／连接中断 | 模式、录课暂停、实际设置与恢复记录显示未知；清除配置检查结果，暂停切换、恢复及重试 |
| 发起新的操作 | 先前查询的快照或错误不再更新页面；不自动重发切换 |
| 配置检查期间断连或发起操作 | 忽略过期报告，提示重新检查；后续新检查可正常完成 |
| 操作回执缺少状态快照 | 当前状态转为未知，等待后台重新确认 |
| 本地取消管理验证 | 保留原有已知状态，恢复相应按钮并显示取消提示；没有执行切换 |
| 后台重新提供快照 | 按新状态恢复控制；未完成切换仍允许恢复，阻止直接开始另一次模式切换 |

仅修改 App 的课堂模式窗口及测试。Host、远程考试流程、管理验证、配置修订、Windows 自启动与软件运行控制规则不变。配置检查提供内部依赖注入入口，生产构造函数仍使用原有读取与管理员任务检查。

## 验证

- 三处修复前失败证据分别保存在 `.artifacts/classroom-status/before/result.json`、`ordering-before/result.json`、`setup-before/result.json`。
- 5 组隔离 WPF 检查通过：真实只读请求的旧查询时序、迟到配置报告及新检查恢复、失联后新查询恢复、无快照回执与合成取消反馈、配置检查被新核实取代后不再显示等待或覆盖操作提示。请求使用真实 App 客户端和唯一模拟 Host 管道。
- 已查看 700×620 最小窗口中的未知提示与底部恢复区域截图。
- 44/44 项既有课堂模式、运行切换及首次配置检查回归通过，0 跳过。App 与界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ClassroomModeTests|FullyQualifiedName~ClassroomRuntimeTests|FullyQualifiedName~ClassroomSetupTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/classroom-status/wpf" --classroom-status
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/classroom-status/`，不随 Git 分发。新增测试只发送状态查询和只读核实，不发送模式切换；配置、任务和软件状态全部合成。取消反馈由模拟响应提供，未测试真实管理验证弹窗。未启动教学软件、操作真实管理员任务、请求 UAC 或修改 Windows 自启动。真实切换、班级大屏及生产验收未运行；本轮不含推送、部署或发布。
