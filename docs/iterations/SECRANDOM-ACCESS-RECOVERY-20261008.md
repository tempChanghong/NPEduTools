# SecRandom：后台访问拒绝后的恢复

2026-10-08，基线 `8f3ca85`。只补齐点名窗口的后台管道访问拒绝处理。

## 问题与修复

Windows 拒绝打开后台管道时，HostClient 会抛出 `UnauthorizedAccessException`。点名窗口的三处请求处理只捕获了断线、超时等错误，漏掉此异常：状态轮询因此退出并保留旧的“就绪”快照；页面操作与侧栏闪抽的任务则向调用者抛出异常。

三处处理现在均捕获权限拒绝，沿用连接失效流程：禁用抽取、显示状态未确认、把旧结果标为历史，并继续轮询。权限恢复后由新状态恢复按钮；不自动重发抽取，不改变管道权限或后台授权规则。

## 验证

- 修复前：三条路径均失败。轮询未清除旧就绪状态；页面检查接口和侧栏闪抽分别抛出权限拒绝异常。
- 修复后：三条路径均进入未知状态，保留明确标注的历史回执；更换为可访问的测试管道后，轮询恢复。恢复时只发送 `secrandom.status`，不重发命令。与已有 5 项检查合计 8 项隔离 WPF 检查通过。
- 界面测试项目及 Release 解决方案构建：0 警告、0 错误。完整回归 868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。207 份 Markdown、948 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/secrandom-access-20261008/after'), '--secrandom-status') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '点名后台访问恢复检查失败' }
```

本机证据位于 `.artifacts/secrandom-access-20261008/` 的 before、after 与 full。使用唯一模拟管道：只写服务端让真实双向客户端收到权限拒绝，随后以双向服务端验证恢复；不修改真实管道 ACL、用户权限或注册表。不连接学校，不运行真实 Host 或 SecRandom，不执行真实抽取。真实设备、部署和发布未测试。
