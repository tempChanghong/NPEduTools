# 单次录课：刷新时保留未保存的编辑

2026-10-08，基线 `341dae5`。只修复同一录课时段重新选中时的编辑内容覆盖。

## 问题与修复

编辑已保存的单次录课时段时，后台课表刷新可能改变该行的计划说明。例如新增课程与此时段重叠，计划表需要显示新的冲突提示。重建表格会重新选中同一时段，原选择事件随即把保存的旧名称、开始时间和结束时间重新填入编辑框，覆盖尚未保存的修改。

现在重新选中的条目仍是当前编辑编号时，不再重填编辑框。计划表和冲突提示继续刷新，草稿不会自动保存；用户切换到其他条目时仍载入该条目的内容。显式保存继续更新原编号。

## 验证

- 修复前：使用模拟后台管道返回新课表，新增重叠课程后，真实 WPF 编辑框中的三项未保存输入均被覆盖。仅更新课表版本、不改变该行内容的对照通过。
- 修复后：两种刷新均保留输入和编辑编号，磁盘不自动保存草稿；计划表显示新增的重叠提示。显式保存更新原条目、不产生重复，切换到另一条目仍正确载入。共 4 项新检查及已有 13 项录课计划检查通过。
- 界面测试项目及 Release 解决方案构建：0 警告、0 错误。完整回归 868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。206 份 Markdown、946 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/recording-dated-edit-20261008/after'), '--recording-plan-save') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '单次录课编辑刷新检查失败' }
```

本机证据位于 `.artifacts/recording-dated-edit-20261008/` 的 before、after 与 full。使用唯一模拟管道、合成课表、真实 WPF 输入与按钮、隔离配置；不运行真实 Host、ClassIsland 或录制器，不采集屏幕或音频，不连接学校。真实设备、部署和发布未测试。
