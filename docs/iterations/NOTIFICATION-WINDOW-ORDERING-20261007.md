# 通知窗口替换后的正文核验 · 2026-10-07

[迭代索引](README.md) · [正文有效性](NOTIFICATION-VALIDITY-RECOVERY-20261007.md)

**交付：旧窗口的正文核验回执不再影响替换后的通知窗口。**

修复前，挂起旧窗口的正文 get，关闭旧窗口，再设置重新打开的同一通知窗口或另一归属的新通知窗口。旧回执成功、通知撤回、协议版本错误或断连时，会使新窗口失效，或错误刷新新窗口的核验时间。8 个替换场景失败；当前窗口未变化的成功／撤回对照通过，见本机 .artifacts/notification-window-ordering/before/result.json。

正文核验保存发起查询时的窗口、通知对象及归属。处理回执前确认三者仍对应当前展示；目标已变化时结束旧核验，既不修改新窗口，也不延长核验期限。旧目标的查询异常同样不清空当前列表或覆盖其提示。下一轮继续查询新窗口的准确通知版本，取得新窗口自身的核验回执后才刷新时间。

当前窗口未变化时，正常回执、撤回提示及原有异常／60 秒未核实保留期限处理继续生效。未修改展示／关闭回执或操作占用。

## 验证

- 隔离 WPF：53/53 组通过，含新增 10 组窗口核验时序检查及既有 43 组通知检查。
- 新增场景检查旧回执对新窗口正文、当前列表和核验时间的影响，以及下一轮对新窗口的准确核验；覆盖重新打开同一通知和切换到另一归属的通知。窗口未变的有效／撤回回执继续正确应用。
- NotificationTests：10/10 通过，0 跳过。App 与界面测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-window-ordering/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。只连接唯一模拟管道，不调用 MainWindow.Start；通知窗口仅构造、关闭和读取字段，不显示、不获取真实操作占用，不发送展示／关闭回执。没有启动真实 Host、学校连接、通知弹窗、录制、麦克风或守护。实际弹窗关闭重开、班级大屏及生产验收未运行。
