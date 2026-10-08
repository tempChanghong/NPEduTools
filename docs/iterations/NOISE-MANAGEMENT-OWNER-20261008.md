# 管理验证：关闭窗口后的迟到状态

2026-10-08，基线 `8872732`。只修复桌面管理验证入口的窗口生命周期，不改口令规则、保护范围、票据验证或后台操作。

## 问题与修复

读取后台管理状态期间关闭所属窗口，设置口令和管理授权入口仍可能尝试打开模态窗口，触发“无法将 Owner 属性设置为已关闭的 Window”异常。后台返回无保护状态时，授权入口还会返回原操作，继续交给调用方处理。

状态查询现在监听所属窗口的 `Closed` 事件。查询完成时若窗口已经关闭，丢弃结果；配置入口直接返回，授权入口返回取消，不打开弹窗或返回待执行操作。监听在查询结束后移除。使用明确的关闭事件，因为 WPF 的 `IsLoaded` 在关闭后、卸载事件处理前可能仍为真；正常隐藏窗口不被视为已关闭。

查询仍有原来的五秒超时，仅查询管理状态，不重发操作，也不撤销已经发给后台的独立口令提交。

## 验证

- 修复前：挂起管理状态查询，关闭窗口后返回正常状态。配置和受保护授权两项均复现窗口异常，无保护授权返回了原操作；对应的三个存活窗口对照通过。
- 修复后：三项关闭检查、三项正常窗口对照、三项隐藏窗口对照及已有管理协议错误／重试和噪音界面回归，共 37 项隔离 WPF 检查通过。关闭窗口不打开弹窗、不返回操作；正常与隐藏窗口仍可打开设置／授权弹窗，无保护状态仍允许继续原操作。
- 界面测试项目及 Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。203 份 Markdown、940 个本地链接及差异检查通过。

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$run = Start-Process -FilePath ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe -ArgumentList @((Join-Path (Get-Location) '.artifacts/noise-management-owner-20261008/after'), '--noise-status') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '噪音管理隔离检查失败' }
```

本机证据位于 `.artifacts/noise-management-owner-20261008/` 的 before、after 与 full。使用唯一模拟管道、真实窗口、合成状态和口令；不运行真实 Host、采集麦克风、修改管理口令或停止实际监测。真实设备、部署和发布未测试。
