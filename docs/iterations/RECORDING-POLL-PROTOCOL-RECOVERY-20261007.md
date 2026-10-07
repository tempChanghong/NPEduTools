# 录制状态轮询协议恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：无效状态或租约回执不会终止录制客户端轮询，正常回执返回后自动恢复。** 修复前模拟后台返回请求编号不匹配的回执，复现轮询任务异常退出、页面仍保留旧已确认状态。

问题在于协议校验抛出的 InvalidDataException 不属于 IOException，原轮询异常过滤器没有捕获它。本轮仅将该异常纳入原有失联与重试分支。

| 情况 | 处理 |
| --- | --- |
| 无效状态或租约回执 | 拒绝回执，通知手动和自动录课界面当前状态未知，保留旧会话供核对 |
| 后台重新提供有效回执 | 同一个客户端继续轮询并恢复实际状态，无需重启应用 |
| 旧查询迟到 | 继续使用原请求代次判断，不覆盖较新的操作结果 |

协议版本、请求编号及帧长度校验仍然执行。原轮询间隔、录制命令、租约及录制器截止策略未调整；恢复时不自动提交启动、停止或开关命令。

## 验证

- 修复前证据：.artifacts/recording-poll-recovery/before/result.json，错误为 request-id: malformed response terminated recording polling: Response correlation or protocol mismatch.
- 7 组新增隔离检查通过：错误请求编号、错误协议版本、零长度帧、过大帧、空消息、JSON 类型错误、错误租约回执。每组验证保留旧会话、通知状态未知及正常轮询恢复。
- 14 组已有回归通过：手动录课恢复 5 组、自动录课回执恢复 5 组、自动录课状态界面 4 组。
- 27/27 项协议、录制契约与执行策略测试通过，0 跳过；App 和界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.ProtocolTests|FullyQualifiedName~NPEduTools.Tests.RecordingTests|FullyQualifiedName~RecordingExecutionTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-poll-recovery/wpf" --manual-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-poll-recovery/automatic-recovery" --automatic-recovery
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-poll-recovery/automatic-status" --automatic-recording
node scripts/check-doc-links.mjs
```

新增检查使用唯一模拟管道、真实录制客户端和合成状态，不创建真实录制会话。未启动真实 Host、录制组件或采集设备；真实录制、教室设备和生产验收未运行。本轮未推送、部署或发布。
