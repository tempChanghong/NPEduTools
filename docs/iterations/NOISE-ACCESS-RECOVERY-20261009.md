# 噪音监测后台访问恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

2026-10-09，基线 `5de7424`。**交付：后台管道拒绝访问后，噪音页保持可恢复，管理验证保留显式重试。**

修复前，`UnauthorizedAccessException` 未进入噪音页和管理口令窗口的异常处理。隔离管道复现六条失败路径：轮询退出并保留旧采样事实；保存麦克风、停止监测、打开管理设置、配置管理口令和验证停止权限漏出异常。

仅在四处现有处理加入此异常。噪音页沿用未知状态展示，清空旧电平、统计和趋势，禁用采集操作，并继续只读查询；权限恢复后读取新状态，不重发选择、启动或停止请求。管理入口显示重试提示；口令窗口不授予权限、不关闭，只有再次明确提交才重试。Host 的权限、管道 ACL、管理口令规则与授权范围不变。

## 验证

- 修复前六项均失败，记录在 `.artifacts/noise-access-20261009/before/result.json`。
- 修复后六项新增检查与已有噪音界面／协议／管理回归合计 **43 项通过**，记录在同目录的 `after/result.json`。覆盖状态失效、只读恢复、操作不重放和手动管理重试的原实例／会话／目标范围。
- 噪音与协议专项单元测试 **263 项通过，0 失败、0 跳过**，TRX 位于同目录的 `tests/noise.trx`。界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$check = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/noise-access-20261009/check'), '--noise-status') -WindowStyle Hidden -PassThru -Wait
if ($check.ExitCode -ne 0) { throw '噪音后台访问恢复检查失败' }
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~Noise|FullyQualifiedName~ProtocolTests'
```

界面检查使用唯一模拟管道：只写服务端让真实双向客户端收到访问拒绝，随后更换为双向服务端验证恢复；设备、采样、管理口令和授权均为合成数据。不修改真实用户、注册表或管道权限，不运行真实 Host、采集麦克风或连接学校服务。本轮未执行完整解决方案回归、真实设备或生产验收；提交保留本地，未推送、部署或发布。
