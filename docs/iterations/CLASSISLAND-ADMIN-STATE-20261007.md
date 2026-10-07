# ClassIsland 管理状态恢复 · 2026-10-07

[迭代索引](README.md) · [使用指南](../GETTING-STARTED.md)

**交付：管理员检查失联或程序路径变更后，不再显示旧运行权限与插件状态。** 修复前在真实主窗口处理方法中注入一次读取异常：任务显示待核实，进程仍显示管理员运行，插件仍显示已安装。

| 情况 | 页面行为 |
| --- | --- |
| 检查异常或操作没有确定回执 | 任务、运行权限、插件统一待核实；清除旧任务指纹，禁用创建和删除 |
| 已保存路径变化，包括旧请求随后返回 | 丢弃旧路径结果，等待新检查 |
| 取消 Windows 授权 | 保留同一路径的已知状态，不认定发生更改 |
| 新检查成功 | 恢复当前路径状态与可用按钮，不重发原操作 |
| 路径输入尚未保存 | 提醒保存并暂停管理；撤回输入后恢复该已保存路径的已知状态 |

仅修改 App 的展示与快照失效处理。管理员工具、UAC、任务归属核验、启动和退出策略均未修改。内部操作入口允许界面测试注入替身；正常按钮仍调用原管理员客户端。

## 验证

- 修复前失败证据：`.artifacts/admin-status/before/result.json`。
- 5 组隔离 WPF 检查通过：读取异常、模拟删除丢失回执、更换路径、旧路径回执迟到、模拟取消管理员检查；均验证新查询恢复、无自动重发、未保存输入及撤回。
- 23/23 项 `AdminStartupTests` 纯启动策略与任务 XML 检查通过，0 跳过。
- App 与界面测试项目 Release 构建通过，0 警告、0 错误；查看了 840×600 主窗口的未确认状态截图。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~AdminStartupTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/admin-status/wpf" --admin-status
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/admin-status/`，不随 Git 分发。界面只使用合成状态与操作替身；没有启动真实管理员工具、UAC、ClassIsland 或 Host，没有读取或修改 Windows 计划任务，也未连接学校。真实设备、班级大屏与生产验收未运行；本轮未推送、部署或发布。
