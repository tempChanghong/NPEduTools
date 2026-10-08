# 初始设置：学校时间查询与页面导航

2026-10-08，基线 `fe8a9cc`。只修复初始设置中“连接学校时间”的异步查询，不改桥接协议、录制策略或时间校验条件。

## 问题与修复

检查程序位置、ClassIsland 连接或学校时间期间，用户返回上一步再重新进入，旧查询仍可能更新新页面。旧连接错误也会覆盖新页面提示；重新进入时因旧查询占用忙碌标记，不能立即开始完整的新检查。

每次检查现在绑定发起时的引导状态，在三个异步读取结束及异常处理前确认仍是同一次访问。失效结果不更新控件或时间跟踪器，不继续旧查询链。旧检查释放忙碌标记后，如果用户已重新进入该页，立即从程序配置开始完整检查；关闭窗口后不再重启查询。

## 验证

- 修复前：三个查询阶段分别挂起，再导航离开并返回，释放旧成功回执或模拟断连；6 个隔离 WPF 用例全部复现旧结果／错误改写页面。
- 修复后：上述 6 项和已有的 3 项准备页导航检查，共 9 项通过。停止定时轮询后仍能立即重新读取；确认新配置显示、不可用学校时间不能勾选确认、引导没有被自动标记完成。
- Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。202 份 Markdown、938 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/oobe-clock-visit-20261008/after'), '--onboarding-preparation') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '初始设置隔离检查失败' }
```

本机证据位于 `.artifacts/oobe-clock-visit-20261008/` 的 before、after 与 full。使用唯一模拟管道、真实 WPF 控件和仅检查存在性的临时文件，不启动真实 ClassIsland、Host 或采集，不连接学校。真实设备、部署和发布未测试。
