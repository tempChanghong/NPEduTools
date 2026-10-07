# 通知查询期间翻页 · 2026-10-07

[迭代索引](README.md) · [通知分页边界](NOTIFICATION-PAGINATION-RECOVERY-20261007.md)

**交付：用户翻页后，上一页的迟到回执不再覆盖新选择。**

修复前，挂起第三页查询，再通过真实“上一页”按钮选择第二页或第一页：旧页成功回执会重新填入第三页条目；旧页缩减为空或查询失败会重置新页码，或覆盖加载提示。8 个组合均已复现失败，证据为本机 .artifacts/notification-page-ordering/before/result.json。

每次翻页递增导航版本，并清空旧条目、列表归属和下一页标记，显示“正在读取通知列表…”。轮询只将同一导航版本的列表结果或错误提示写入页面；下一轮查询使用新页码。收到新页前可继续向前翻页，“下一页”需等待新页回执。未新增请求或重试循环。

列表结果过时仍需核对已显示通知的准确版本。旧请求失败时，已显示正文仍遵守原有 60 秒未核实保留期限，不因翻页延长，也不发送展示或关闭回执。

## 验证

- 隔离 WPF：34/34 组通过，含新增 8 组查询期间翻页检查及既有 26 组分页、列表恢复和正文有效性检查。
- 新增检查覆盖旧页正常、旧页为空、缺失列表及协议版本错误，各测试一次／连续两次向前翻页；检查新页查询偏移和条目恢复，并核对成功时的正文 get、失败时的正文失效。
- NotificationTests：10/10 通过，0 跳过。App 与界面测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-page-ordering/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。测试只连接唯一模拟管道，不调用 MainWindow.Start；正文窗口仅构造并读取字段，不显示、不获取操作占用。没有启动真实 Host、学校连接、通知弹窗、录制、麦克风或守护。班级大屏与生产验收未运行。
