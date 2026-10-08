# 定时监测：运行中更新时段

2026-10-08，基线 `0d55e02`。范围仅为桌面 Host 的定时监测时段归属，不改网页、KV、协议、权限或采样算法。

## 问题与修复

19:00–20:00 的监测仍在运行，管理员把规则改为 20:00–21:00，并在学校时间 20:00 应用。执行器继续使用同一个采集会话，但保护信息及停止／故障记录仍写旧时段。旧时段与新时段不重叠，本机或网页停止后下一轮会重新启动，采集故障也会误重试。延长原时段时，保护信息同样不会更新。

现在执行器决定继续采集时，同步并持久保存当前已应用时段。保护查询、人工停止的跳过记录和故障抑制记录都使用该时段。保存失败则停止排程所属采集并报告 `SCHEDULE_STORE_UNAVAILABLE`；没有变化的轮询不重复写文件。正常更新保持同一采集会话，不重新打开麦克风。手动监测仍不受排程接管。

## 验证

- 修复前：新增 8 项检查中 7 项失败、1 项对照通过。三个跨时段用例实际出现 `CAPTURE_STARTING`，证明停止／故障后误启动；其余用例检出旧保护时段及缺少持久保存。
- 修复后：179 项排程、管理验证、展示和日历相关回归通过，0 失败、0 跳过。覆盖延长／移动时段、本机停止、网页 STOP、采集故障、记录落盘、写入失败及不变规则不写文件。
- Release 解决方案构建：0 警告、0 错误。
- 完整解决方案回归：864 项桌面测试 + 183 项 NPEP 测试，共 1047 项通过，0 失败、0 跳过。200 份 Markdown、934 个本地链接与差异检查通过。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore -c Release '-m:1' --filter 'FullyQualifiedName~NoiseScheduleHostTests|FullyQualifiedName~NoiseScheduleTests|FullyQualifiedName~NoiseManagementTests|FullyQualifiedName~NoiseDisplayTests'
```

本机证据位于 `.artifacts/noise-window-update-20261008/` 的 before、before-stop、before-storage、after 与 full。只使用合成时钟、合成采集对象及隔离目录；不启动真实麦克风、学校连接或桌面窗口。真实大屏、部署和发布未测试。
