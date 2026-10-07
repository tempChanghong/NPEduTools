# 通知列表读取失败后的恢复 · 2026-10-07

[迭代索引](README.md) · [使用指南](../GETTING-STARTED.md)

**交付：读取失败后清空未确认的通知列表与分页标识，恢复后重新读取第一页。**

修复前，实际 NotificationPollAsync、HostClient 与唯一模拟管道复现 12 组失败：请求标识不匹配、版本不匹配、零长度帧、JSON 空消息、断连或缺少收件箱状态后，旧通知条目、归属标识和分页按钮仍保留。每种情况分别检查后续新列表和空列表恢复。

生产代码仅在既有 InvalidateUnverifiedNotification 方法中清理页面列表、归属与分页状态。原有轮询继续工作，无新增请求类型。

| 情况 | 页面结果 |
| --- | --- |
| 读取失败或回执没有收件箱 | 保留既有错误提示；旧条目移除，分页按钮禁用，页码重置 |
| 有效新列表恢复 | 从第一页显示最新归属和条目，按最新分页标识恢复下一页按钮 |
| 有效空列表恢复 | 显示有效通知 0 条，无条目且分页按钮禁用 |

这里清理的是 App 页面缓存。Host 通知存储、投递／关闭回执和已弹出通知的 60 秒有效性核对规则不变。

## 验证

- 修复前：12 组隔离 WPF 检查均失败，见本机 .artifacts/notification-inbox-recovery/before/result.json。
- 修复后：12/12 组通过。使用真实下一页事件和持续轮询，核对未知列表失效、归属替换、第一页恢复及空列表区分。
- 通知专项 NotificationTests：10/10 通过；ProtocolTests：21/21 通过，均 0 跳过。
- App 与界面测试 Release 构建：0 警告、0 错误。已查看读取失败与恢复后的列表截图。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-inbox-recovery/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。界面检查只初始化 MainWindow 的 XAML 与指定通知轮询，不调用 Start；只向唯一模拟管道发送 poll，合成列表均禁止弹窗，无 get／displayed／dismissed 请求。未启动真实 Host、学校连接、通知遮罩、录制、麦克风或守护。真实学校通知、已弹出窗口断连处理和班级大屏验收未运行。
