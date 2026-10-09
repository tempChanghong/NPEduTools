# 课堂模式后台访问恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

2026-10-09，基线 `0f5fbff`。**交付：课堂模式遇到后台访问拒绝时，旧状态失效，连接恢复后继续只读核实。**

修复前，唯一模拟管道拒绝客户端访问，复现三条失败路径：主页轮询因 `UnauthorizedAccessException` 退出；课堂模式管理页轮询停止并保留旧模式和恢复按钮；手动核实请求漏出异常。

只在这三处现有异常处理加入访问拒绝。主页将模式及暂停状态标为未知；管理页清空当前模式、实际自启动、恢复记录与配置检查，禁用切换等操作，解除请求等待。继续 `classroom.status` 轮询，收到新状态后恢复相应控件，不自动重发核实或切换请求。Host 权限、管理验证、模式切换和自启动规则不变。

## 验证

- 修复前三项均失败：`.artifacts/classroom-access-20261009/before/result.json`，记录轮询退出、旧状态保留及手动请求异常。
- 修复后的课堂模式隔离界面检查 **23 项通过**，记录在同目录 `after/result.json`。新增三项验证状态失效、按钮禁用、只读恢复与正常日常状态；同入口运行已有协议、主页轮询、请求时序、配置检查和断连恢复回归。
- 课堂模式、运行切换、配置检查与协议专项单元测试 **68 项通过，0 失败、0 跳过**，记录在同目录 `tests/classroom.trx`。界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$check = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/classroom-access-20261009/check'), '--classroom-status') -WindowStyle Hidden -PassThru -Wait
if ($check.ExitCode -ne 0) { throw '课堂模式界面检查失败' }
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~Classroom|FullyQualifiedName~ProtocolTests'
```

新增检查通过只写模拟管道拒绝真实双向客户端，再更换为双向管道读取合成日常状态。管理页的配置检查使用替身，显式请求仅为只读核实；不启动真实 Host、ClassIsland、ExamAware 或管理员工具，不修改实际管道 ACL、自启动或学校配置。本轮未执行完整解决方案回归、真实设备或生产验收；提交保留本地，未推送、部署或发布。
