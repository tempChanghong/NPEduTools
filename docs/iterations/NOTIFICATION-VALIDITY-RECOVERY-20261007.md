# 通知正文核对失败的保留期限 · 2026-10-07

[迭代索引](README.md) · [通知列表恢复](NOTIFICATION-INBOX-RECOVERY-20261007.md)

**交付：列表查询成功不会延长核对失败的通知正文保留期限。**

修复前，NotificationPollAsync 在列表成功后立即更新时间，再查询正在显示通知的具体版本。若第二次查询反复失败，60 秒保留期限就会反复重新开始，旧正文可能一直不会隐藏。10 组模拟管道检查均复现了不正确的时间更新。

生产代码仅把核对时间更新移到本轮查询完成之后。正在显示通知的具体版本查询失败时，保留上次成功核对时间；继续复用原有 60 秒规则、失效提示与正文替换，不修改通知存储或回执。

| 情况 | 结果 |
| --- | --- |
| 列表成功，正文核对失败，距上次成功核对不足 60 秒 | 暂时保留正文，但不延长期限 |
| 列表成功，正文核对失败，距上次成功核对超过 60 秒 | 隐藏旧正文，显示无法核对提示 |
| 保留期内取得当前归属及具体版本的有效回执 | 更新时间，继续保留当前正文 |
| 正文已失效后列表恢复 | 保持失效提示，不自动恢复旧正文或发送关闭回执 |

## 验证

- 修复前：10 组新增检查失败，见本机 .artifacts/notification-validity-recovery/before/result.json。
- 修复后：22/22 组隔离 WPF 检查通过，包括 10 组正文核对和 12 组既有列表恢复。
- 正文检查覆盖请求标识错误、版本错误、零长度帧、JSON 空消息和断连，各验证 20 秒／61 秒两种合成核对年龄；不是现场等待一分钟的实测。
- 使用真实通知窗口的 XAML 字段验证失效和正文替换，但从未显示该窗口，不触发置顶、遮罩或展示回执。有效恢复仍查询原归属与具体通知版本，不自动重开失效正文。
- NotificationTests：10/10；ProtocolTests：21/21，通过且 0 跳过。App 与界面测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-validity-recovery/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。只初始化隔离 MainWindow 与指定轮询，不调用 Start；新增检查只向唯一模拟管道发送 poll／get，没有 displayed／dismissed 请求。未启动真实 Host、学校连接、通知弹窗、录制、麦克风、守护或运行切换。真实弹窗、班级大屏与生产验收未运行。
