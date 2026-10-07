# 手动录课断连与恢复 · 2026-10-07

[迭代索引](README.md) · [录课指南](../GETTING-STARTED.md#手动录课)

**交付：后台失联时，录课窗口、主页和侧边栏不再把旧时长当作当前状态。** 原客户端查询失败只通知自动录课页面，手动录课仍显示旧的“录制中”。真实客户端查询唯一的不存在管道，已复现该问题；本机失败证据在 `.artifacts/manual-status/before/result.json`。

| 情况 | 界面与客户端行为 |
| --- | --- |
| 尚未收到快照／连接中断 | 显示状态未知，隐藏旧时长与统计，禁用录制操作；不据此认定录制已停止 |
| 操作请求丢失回执 | 保留之前已确认的会话信息，标记未知；不把乐观的“准备中”当作实际结果 |
| 较早查询随后失败 | 若期间已发起新操作，不覆盖后来的成功回执 |
| 新快照确认恢复 | 按真实阶段恢复显示与操作；准备录制时清除旧统计及对应断连错误，保留无关的设置校验提示 |

修改 App 的录制客户端与三个展示入口，新增界面测试使用的设备探测依赖注入。Host、录制器、协议、计划、会话校验、截止及租约保护不变；网页与 KV 未改。最后已知会话保留在客户端，仅供恢复核对和既有退出检查，界面不宣称其仍在运行。

## 验证

18 项既有录制契约／执行回归通过。App 与界面测试项目 Release 构建通过，0 警告、0 错误。隔离检查通过：

- 真实客户端查询不存在管道后通知手动录课失联。
- 真实录课窗口与侧边栏正确显示未知、恢复及已保存结果；恢复后清除断连错误，保留无关设置提示。已检查 540×630 窗口的顶部、滚动说明和恢复截图。
- 唯一模拟管道覆盖暂停的会话校验、旧查询失败、新查询恢复、未知状态禁止新开始以及开始回执丢失。
- 共用客户端改动后，自动录课的 4 组隔离 WPF 检查仍通过。

恢复后的旧错误也先得到失败证据，位于 `.artifacts/manual-status/recovery-error-before/result.json`；最终证据在 `.artifacts/manual-status/wpf/`、`unit/` 和 `automatic-regression/`，不随 Git 分发。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~RecordingTests|FullyQualifiedName~RecordingExecutionTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/manual-status/wpf" --manual-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/manual-status/automatic-regression" --automatic-recording
node scripts/check-doc-links.mjs
```

测试只使用唯一管道和合成设备／回执，不启动 Host、真实设备探测或录制器，不生成视频；侧边栏只构造并核对控件，不显示或激活桌面停靠。完整主页操作、真实录课、班级大屏及生产验收未运行。本轮不包含推送、部署或发布。
