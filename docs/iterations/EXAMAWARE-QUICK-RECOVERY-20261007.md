# 考试看板快捷入口恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：侧边栏打开考试看板时，无效启动回执不再逃出界面事件，而是打开管理页等待核实。** 修复前，唯一模拟后台返回不匹配的请求编号，真实 async-void 事件抛出未处理异常，管理页没有打开。

本轮仅将 InvalidDataException 纳入快捷入口已有的失败处理。请求编号、协议版本和消息长度校验仍然保留；不能从无效回执判断启动结果。管理页先显示后台连接未确认，再只读查询当前状态，不自动重发启动命令。后台正常接受启动、明确拒绝及应用关闭时的原有处理保持不变。

## 验证

- 修复前证据：.artifacts/examaware-quick-recovery/before/result.json，记录请求关联错误逃出真实快捷事件；测试夹具捕获该异常并报告失败，不修改生产异常处理。
- 8 组新增隔离 WPF 检查通过：请求编号错误、版本错误、零长度帧、空消息、断连、明确拒绝、正常接受、等待回执时关闭应用。
- 需要管理页的六种情况均验证未知提示、新状态查询及恢复；查询为 examaware.status，没有重发 examaware.start。正常接受不额外打开管理页，等待时关闭不因取消请求重新打开窗口。
- 5 组已有考试看板界面回归通过，覆盖方案与查询顺序、断连、操作回执丢失、恢复与无效文件拦截、配对导出错误。
- 70/70 项 ExamAware 及协议测试通过，0 跳过。界面测试项目 Release 构建通过，0 警告、0 错误。
- 已查看管理页未知状态和恢复状态截图，位于 .artifacts/examaware-quick-recovery/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ExamAwareTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/examaware-quick-recovery/wpf" --examaware-status
node scripts/check-doc-links.mjs
```

仅连接唯一模拟管道，使用隔离主页和合成考试状态。没有启动真实 ExamAware、Host 或管理员工具，没有放映、修改 Windows 自启动或真实配对。真实软件、教室设备和生产验收未运行；本轮未推送、部署或发布。
