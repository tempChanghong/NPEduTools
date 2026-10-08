# 返回作业板：校时后的期限恢复

2026-10-08，基线 `6999518`。只修复 Host 的已确认返回期限缓存，不改网页、KV、协议、监测启停或管理授权。

## 问题与修复

定时监测期间暂时返回作业板，Windows 时间被向前或向后调整，随后收到学校对返回期限的回执。原实现保留旧开始时间，却按新时间写入到期时间，可能得到“到期早于开始”或“跨度超过允许分钟数”的记录。当前单调倒计时仍正常，但重启 Host 后文件校验失败，返回功能被 `DISPLAY_STORE_UNAVAILABLE` 阻断。

学校确认后的缓存现在使用同一次本机时间读取计算开始与到期时间，跨度只包含当前剩余时长；当前进程仍使用原来的单调期限，学校回执不能延长它。尚未确认的离线申请仍保留原始开始时间与请求 ID，以供补登记；没有联网确认时，重启遇到回拨仍沿用原有保守处理。

## 验证

- 修复前：4 个“校时 → 合法学校回执 → 重启”用例全部失败，均出现 `DISPLAY_STORE_UNAVAILABLE`。分别覆盖提前／回拨 20 分钟，以及首次确认离线申请／刷新已确认期限。
- 修复后：77 项展示、排程执行及管理保护回归通过，0 失败、0 跳过。确认重启前剩余 550 秒，经过 1 秒重启后剩余 549 秒；监测保持运行，不重复补登记。
- Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：868 项桌面测试 + 183 项 NPEP 测试，共 1051 项通过，0 失败、0 跳过。201 份 Markdown、936 个本地链接与差异检查通过。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~NoiseDisplayTests|FullyQualifiedName~NoiseScheduleHostTests|FullyQualifiedName~NoiseManagementTests'
```

本机证据位于 `.artifacts/noise-return-clock-20261008/` 的 before、after 与 full。使用合成时钟、合法模拟回执、合成监测状态与隔离目录，没有调整 Windows 时间、使用真实麦克风或连接学校。真实设备、部署和发布未测试。
