# 手动打开通知的查询时序 · 2026-10-07

[迭代索引](README.md) · [通知翻页时序](NOTIFICATION-PAGE-ORDERING-20261007.md)

**交付：手动打开通知的旧查询不再覆盖新页面提示，并保留点击时的列表归属。**

修复前，挂起正文 get 后点击“上一页”，或由并行 poll 刷新为新归属列表；旧请求返回不可展示结果、协议版本错误或断连时，均会覆盖当前加载／列表提示。6 个场景失败，3 个页面未变的对照场景通过，见本机 .artifacts/notification-open-ordering/before/result.json。

打开请求保存点击时的列表归属和导航版本。回执返回后，如果窗口已关闭、用户已翻页或列表归属已经变化，就结束这次打开；过时失败不改新提示。页面仍对应原请求时，继续显示“通知已变化”或“无法打开”的反馈。进入已有展示流程时使用保存的归属，不再次读取可能已经变化的列表字段。

按钮事件调用可等待的异步方法，便于直接核查这次打开是否结束；未修改 Host、通知缓存、展示／关闭回执或正文展示前的二次核对。

## 验证

- 隔离 WPF：43/43 组通过，含新增 9 组手动打开时序检查和既有 34 组通知检查。
- 新增检查使用唯一模拟管道：真实“上一页”按钮、并行 poll 更新归属、三类旧 get 失败及页面未变的对照；核查请求的准确归属／通知版本、打开完成和当前页面数据。调用与按钮相同的异步方法，不显示通知弹窗。
- NotificationTests：10/10 通过，0 跳过。App 与界面测试 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~NotificationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release --no-restore
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/notification-open-ordering/wpf" --notification-inbox
node scripts/check-doc-links.mjs
```

证据目录不随 Git 分发。模拟响应禁止展示；不调用 MainWindow.Start、不获取真实操作占用，没有启动真实 Host、学校连接、通知弹窗、录制、麦克风或守护。成功弹窗、关闭窗口后的查询取消、班级大屏及生产验收未在本轮运行。
