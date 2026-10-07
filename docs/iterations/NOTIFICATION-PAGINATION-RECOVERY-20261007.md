# 通知列表缩减后的分页恢复 · 2026-10-07

[迭代索引](README.md) · [通知列表恢复](NOTIFICATION-INBOX-RECOVERY-20261007.md)

**交付：当前页因通知撤回或过期而越界时，分页按钮立即按重置后的页码更新。**

修复前，模拟用户从第二页进入第三页（偏移 20），通知总数变成 0、3 或 20 时，内部偏移已重置为 0，但“上一页”仍然可用。总数为 25、第三页仍有效的对照场景通过。

生产代码只调整更新顺序：先重置越界偏移，再更新分页按钮。沿用原有下一轮轮询重新读取第一页，不增加即时请求；取得新页回执前，越界页没有条目。仍有效的后续页面不强制跳回第一页。

## 验证

- 修复前：3 个越界场景失败、1 个有效页面对照通过，见本机 .artifacts/notification-pagination-recovery/before/result.json。
- 修复后：26/26 组 WPF 检查通过，含 4 个分页边界场景和 22 个既有通知列表／正文有效性检查。
- 新增场景使用真实下一页按钮、模拟 Host 回执和持续轮询，检查即时按钮状态、下一次请求偏移及新页条目。已查看空列表和第一页恢复截图。
- NotificationTests：10/10 通过，0 跳过。App 与界面测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-pagination-recovery/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。新增分页检查只向唯一模拟管道发送 poll，合成通知禁止弹窗；不调用 MainWindow.Start。没有启动真实 Host、学校连接、通知弹窗、录制、麦克风或守护。实际通知撤回、班级大屏和生产验收未运行。
