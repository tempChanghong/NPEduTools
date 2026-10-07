# 点名回执与断连恢复 · 2026-10-07

[迭代索引](README.md) · [点名指南](../SECRANDOM.md)

**交付：点名页在后台失联或请求结果未确认时，不再沿用旧的就绪状态允许再次闪抽。** 已通过真实 WPF 窗口和唯一模拟管道复现两处问题：请求丢失回执后按钮重新可用；操作前发出的查询随后返回旧快照，覆盖新的“结果待核实”。原有 Host 防重复执行保护仍在，缺陷位于 App 的状态与操作入口。

| 情况 | 行为 |
| --- | --- |
| 首次连接尚未确认／查询失联／操作丢失回执 | 显示后台状态未知，暂停点名操作，继续查询，不自动重发抽取 |
| 有旧执行结果 | 标为“上次已确认的回执”，保留核对信息；旧姓名标为“上次回执”，不当作本次结果 |
| 较早查询返回 | 新操作已开始时，忽略该旧查询的结果或错误 |
| 新快照为待核实 | 按原流程先人工核对 SecRandom 窗口和历史，再解除待核实状态 |
| 新快照确认可用 | 恢复相应操作；未配置时可保存位置，仍不能闪抽 |

只修改 App 点名窗口及测试；不改 Host、协议、配置格式、名单、算法、授权和去重。侧栏仍先获取新状态再提交一次抽取，保留配置修订及请求编号核对。历史展示仅保留窗口已有回执，不增加磁盘记录或学校上传。

## 验证

- 修复前证据：`.artifacts/secrandom-status/before/result.json`、`ordering-before-fixed-harness/result.json`，分别记录丢失回执与旧快照覆盖。后者使用与真实 WPF 事件一致的 Dispatcher 同步上下文。
- 5 组隔离界面检查通过：查询／操作时序、正常查询失联、页面请求丢失回执、历史与未配置状态、侧栏最终回执失联。实际模拟请求保持修订校验，侧栏仅发送一次抽取。
- 已查看 620×540 最小窗口的连接说明及滚动后的历史结果截图。
- Windows PowerShell 5.1 执行 `test-secrandom.ps1 -Configuration Release`，35/35 通过，无跳过。证据：`.artifacts/secrandom-tests/9475262f70e9437e983e2b13475257c9/`。
- App 和界面测试项目 Release 构建通过，0 警告、0 错误。复用界面等待工具后，手动录课的 5 组隔离检查仍通过。

```powershell
./scripts/test-secrandom.ps1 -Configuration Release
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/secrandom-status/wpf" --secrandom-status
node scripts/check-doc-links.mjs
```

界面证据位于 `.artifacts/secrandom-status/`，不随 Git 分发。新增测试使用唯一模拟 Host 管道与虚构姓名，不连接真实 SecRandom，不启动软件，不修改名单或真实抽取历史。真实点名、班级大屏及生产验收未运行，本轮不含推送、部署或发布。
