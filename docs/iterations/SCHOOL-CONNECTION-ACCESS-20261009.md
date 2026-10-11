# 学校互联后台访问恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

2026-10-09，基线 `7a5111d`。**交付：设置页与 OOBE 的共享连接会话处理后台访问拒绝，清除旧状态并允许只读恢复。**

修复前，注入 `UnauthorizedAccessException` 复现刷新、配对、确认归属和解绑前管理验证四条失败路径；真实共享控件的刷新按钮事件也抛出未处理异常。会话没有进入现有失联处理，旧配对状态及确认信息未失效。

只将访问拒绝加入共享会话的现有异常处理。失败后清空当前连接状态、服务与归属确认，禁止配对／确认／暂停／解绑及以旧状态完成 OOBE；保留地址、设备名和输入草稿，释放请求等待。恢复只读取 `npep.status`，不会自动重发配对或解绑请求；需要核对服务或归属时仍须重新确认。不改变 Host 权限、管理验证或配对协议。

## 验证

- 修复前四项新增会话回归均失败，记录在 `.artifacts/school-access-20261009/before/before.trx`；按钮事件失败记录在同目录 `ui/result.json`。
- 修复后学校互联会话、协议与通用协议测试 **66 项通过，0 失败、0 跳过**，记录在 `.artifacts/school-access-20261009/after/tests/school.trx`。
- 设置／OOBE 共享控件与实际引导页 **5 项界面检查通过**，记录在同目录 `after/ui/result.json`。包括真实刷新事件无未处理异常、双控件状态失效和只读恢复；界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~NpepConnection|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$check = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/school-access-20261009/check'), '--school-connection') -WindowStyle Hidden -PassThru -Wait
if ($check.ExitCode -ne 0) { throw '学校互联界面检查失败' }
```

新增检查在会话的请求／管理验证替身中注入异常，使用合成配对状态与真实 WPF 控件；不更改管道 ACL，不启动真实 Host、连接学校服务或创建实际配对。本轮未执行完整解决方案回归、真实设备或生产验收；提交保留本地，未推送、部署或发布。
