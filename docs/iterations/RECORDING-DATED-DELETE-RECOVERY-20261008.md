# 单次录课：删除失败后的重试

2026-10-08，基线 `0e3c337`。只修复单次录课编辑编号的清除时机。

## 问题与修复

选中已保存的单次录课时段后，如果配置文件被占用，删除操作会提示“未应用修改”并保留原计划，但仍清除编辑编号。释放占用后再次删除没有效果；修改名称再保存则新增一条时段，而不是修改原条目。

现在仅在计划删除成功后清除编辑编号。删除失败后仍可显式重试删除，也可继续编辑原条目；保留已有的失败提示和文件保存方式。

## 验证

- 修复前：通过独占写入权限的文件锁，两条实际按钮路径分别复现“删除重试无效”和“保存产生重复条目”；原文件和内存计划均未被失败删除改变。
- 修复后：两条路径均通过，保存继续使用原编号，成功删除后才清除编号；已有 8 项日期导航及 3 项保存／重开检查也通过，共 13 项隔离 WPF 检查。全程不开启试运行。
- 界面测试项目及 Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。205 份 Markdown、944 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/recording-dated-delete-20261008/after'), '--recording-plan-save') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '单次录课删除恢复检查失败' }
```

本机证据位于 `.artifacts/recording-dated-delete-20261008/` 的 before、after 与 full。使用唯一模拟管道、真实 WPF 按钮及隔离文件；不运行真实 Host、ClassIsland 或录制器，不采集屏幕或音频，不连接学校。真实设备、部署和发布未测试。
