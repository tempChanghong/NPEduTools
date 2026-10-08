# 录课计划：切换日期后的课表读取

2026-10-08，基线 `a5ae127`。只修复自动录课计划页的预计课表查询，不改录课规则、实际采集或学校时间校验条件。

## 问题与修复

读取某一天的预计课表时切换日期，新查询会被旧查询的忙碌标记挡住。旧结果虽然已被正确忽略，但旧查询结束后仍无条件设置十秒重试等待，新日期因此暂时没有课表；离开再返回同一天也会触发。旧查询断连时同样如此。

查询结束时现在先释放忙碌标记：如果仍是同一次日期选择，保留原来的十秒等待；如果用户已经切换日期，清除旧等待并尝试读取最新选中的日期。读取仍须通过学校时间检查，返回今天无需读取预计课表，关闭窗口后不再发起查询。旧结果仍不能更新新日期。

## 验证

- 修复前：向后切换、向前切换、离开再返回三种导航，分别覆盖旧成功回执与旧断连，6 项全部复现“新日期继承旧查询的重试等待”；2 项返回今天的对照通过。
- 修复后：8 项日期导航检查和已有的 3 项计划保存／重开检查，共 11 项隔离 WPF 检查通过。停用定时轮询后仍能自动读取最新日期，显示新课表；旧课表被忽略，普通重复读取仍受原等待限制，不开启试运行。
- 界面测试项目及 Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。204 份 Markdown、942 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/recording-calendar-navigation-20261008/after'), '--recording-plan-save') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '录课计划隔离检查失败' }
```

本机证据位于 `.artifacts/recording-calendar-navigation-20261008/` 的 before、after 与 full。使用唯一模拟管道、合成学校时间与课表、真实日期按钮和隔离配置；不运行真实 Host、ClassIsland 或录制器，不采集屏幕或音频，不连接学校。真实设备、部署和发布未测试。
