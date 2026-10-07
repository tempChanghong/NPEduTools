# 触摸辅助状态与操作顺序 · 2026-10-07

[迭代索引](README.md) · [使用指南](../GETTING-STARTED.md)

**交付：触摸辅助的旧查询不再覆盖较新的操作结果。** 修复前通过真实主窗口处理方法和唯一模拟 Host 管道复现：挂起“辅助未开启”的查询，完成开启请求，再返回旧查询，界面错误改回“开启辅助”。

| 情况 | 页面行为 |
| --- | --- |
| 正在开启、停止、暂停或更改兼容选项 | 暂停相关按钮，等待后台确认 |
| 新操作开始后旧查询返回或连接失败 | 忽略过期查询，不退回旧按钮或失联提示 |
| 操作丢失回执 | 保留“暂未确认结果”，暂停控制；不认定开启成功或辅助已停止，不自动重发 |
| 收到新状态 | 按最新运行、暂停和兼容状态恢复控制 |
| 窗口已关闭 | 不再应用随后返回的状态或错误 |

仅修改 App 的触摸辅助查询时序。Host、PowerPoint 判断、触摸手势、输入钩子、兼容模式与启动偏好规则不变。主窗口增加内部界面测试构造入口，省略托盘、配置存储和后台初始化；公开构造函数仍执行原有初始化，正常关闭流程不变。

## 验证

- 修复前失败证据：`.artifacts/touch-status/before/result.json`。
- 3 组隔离 WPF 检查通过：旧查询响应、旧查询连接错误、操作丢失回执；随后均提供新的暂停状态，核对按钮恢复、兼容控件和查询能力，没有自动重发命令。
- 15/15 项 `TouchAssistGestureTests` 合成手势回归通过，0 跳过。真实 Host 输入钩子测试未运行。
- App 与界面测试项目 Release 构建通过，0 警告、0 错误；已查看 840×600 最小主窗口底部的未确认提示与禁用按钮。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~TouchAssistGestureTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/touch-status/wpf" --touch-status
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/touch-status/`，不随 Git 分发。新增界面检查只向唯一模拟管道发送 `presentation.touch.status` 与 `presentation.touch.enable`，返回合成状态；未调用 `MainWindow.Start`，未初始化托盘、持久化配置、真实录制客户端或 Guard。没有启动真实 Host、触摸钩子或 PowerPoint，也没有注入输入、改变 Windows 自启动或连接学校。真实触摸设备、班级大屏和生产验收未运行；本轮不含推送、部署或发布。
