# 录制设置文件恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：录制设置文件版本不支持、内容无效或过大时，录制微课窗口仍能打开并保留原文件。** 修复前使用版本为 999 的隔离设置文件，复现窗口构造抛出 InvalidDataException，无法显示页面。

本轮仅将该异常纳入设置读取的现有处理：显示“原文件已保留”，不应用无效设置；本次可以选择临时选项，保存操作不覆盖异常文件。修复文件后重新打开页面即可重新读取。正常文件仍恢复已保存的屏幕、目录、画质、帧率和音源选项；没有设置文件时使用默认值。

## 验证

- 修复前证据：.artifacts/recording-preferences-recovery/before/result.json，异常来自 RecordingWindow 构造函数的设置校验。
- 7 组新增隔离 WPF 检查通过：不支持的版本、空选项、无效帧率、过大文件、JSON 错误、正常设置和首次使用；校验异常文件没有被保存操作覆盖，并验证修复文件后重开恢复。
- 21 组已有界面／管道回归通过：手动录课界面与连接恢复 5 组、录制轮询协议恢复 7 组、手动命令回执恢复 5 组、自动录课状态界面 4 组。
- 18/18 项录制契约及执行策略测试通过，0 跳过；App 和界面测试项目 Release 构建通过，0 警告、0 错误。最小窗口及滚动至设置提示的截图保存在证据目录。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.RecordingTests|FullyQualifiedName~RecordingExecutionTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-preferences-recovery/wpf" --manual-recording
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/recording-preferences-recovery/automatic-status" --automatic-recording
node scripts/check-doc-links.mjs
```

使用唯一测试端点对应的设置文件和合成设备，结束后清理自己的文件。没有启动真实录制、设备检测组件或 Host，没有修改正常端点的设置。真实录制、教室设备和生产验收未运行；本轮未推送、部署或发布。
