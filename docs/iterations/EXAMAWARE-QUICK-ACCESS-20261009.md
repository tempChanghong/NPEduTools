# 考试看板快捷入口访问恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

2026-10-09，基线 `6b848e8`。**交付：侧栏打开考试看板遇到后台访问拒绝时，不再抛出未处理异常。**

唯一模拟管道拒绝双向连接时，真实 `OpenExamAwareQuick` 按钮事件漏出 `UnauthorizedAccessException`，管理页没有打开。这条 `async void` 事件的异常会进入 WPF 未处理异常流程。

只将此异常加入快捷入口已有失败处理：应用仍在运行时打开管理页，先显示后台连接未确认、禁用放映及自启动操作；连接恢复后通过 `examaware.status` 重新读取。不会自动重发启动请求，不修改 Host 权限或管道 ACL。原有协议错误、明确拒绝、正常接受和应用关闭处理保持不变。

## 验证

- 修复前失败记录：`.artifacts/examaware-quick-access-20261009/before/result.json`，包含真实按钮事件漏出的访问拒绝异常；测试夹具捕获异常并报失败。
- 修复后的考试看板隔离界面检查 **28 项通过**，记录在同目录 `after/result.json`。新增检查覆盖未处理异常、管理页未知状态、操作禁用、只读恢复与正常动作恢复，并运行已有协议、快捷入口、查询顺序、丢失回执和导出检查。
- ExamAware 与协议专项单元测试 **85 项通过，0 失败、0 跳过**，记录在同目录 `tests/examaware.trx`。界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$check = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/examaware-quick-access-20261009/check'), '--examaware-status') -WindowStyle Hidden -PassThru -Wait
if ($check.ExitCode -ne 0) { throw '考试看板界面检查失败' }
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~ExamAwareTests|FullyQualifiedName~ProtocolTests'
```

新增检查先创建只写模拟管道，使真实双向客户端收到访问拒绝，再换成双向管道验证恢复；不修改实际权限。只使用隔离主页、管理页及合成桥接状态，没有运行真实 Host、ExamAware 或管理员工具，不修改真实配对、自启动或放映。本轮未执行完整解决方案回归、真实设备或生产验收；提交保留本地，未推送、部署或发布。
