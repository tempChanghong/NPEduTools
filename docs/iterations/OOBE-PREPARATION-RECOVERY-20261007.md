# 初始设置准备页的查询恢复 · 2026-10-07

[迭代索引](README.md) · [首次使用指南](../FIRST-RUN-EXPERIENCE.md)

**交付：离开准备页后再返回，重新读取当前配置，不把上一次进入时的查询结果当作新检查。** 修复前通过真实“上一步 → 跳过使用偏好 → 返回噪音准备”操作复现：原先挂起的状态查询随后返回，旧监测消息出现在重新进入的页面，且没有发起新查询。

| 情况 | 页面行为 |
| --- | --- |
| 阅读准备页，导航状态未改变 | 正常显示读取结果 |
| 等待读取时离开，再进入同一步 | 忽略旧结果和旧连接错误，随后读取本次页面状态 |
| 离开考试准备页时程序配置读取尚未完成 | 旧读取返回后不继续查询旧页面的考试看板状态 |
| 读取完成 | 只更新准备说明；不自动完成步骤，不更改跳过记录 |

仅修改 `OnboardingWindow.Preparation.cs` 中噪音与考试环境准备页的只读查询，以进入页面时的引导状态对象识别本次访问。点名、学校配对、学校时间、录制设备检查、协议同意及配置执行逻辑不变。

## 验证

- 修复前失败证据：`.artifacts/onboarding-preparation/before/result.json`。
- 3 组新增隔离 WPF 检查通过：噪音旧响应、噪音旧连接错误、考试环境旧配置响应；通过真实导航按钮返回同一步，核对新的只读请求、页面结果及保存的导航记录。
- 4 组既有协议／初始设置界面检查通过，26/26 项 `OnboardingTests` 回归通过，0 跳过。
- App 与界面测试项目 Release 构建通过，0 警告、0 错误；已查看 900×680 的噪音与考试准备页截图。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~OnboardingTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/onboarding-preparation/wpf" --onboarding-preparation
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/onboarding-preparation/existing-wpf"
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/onboarding-preparation/`，不随 Git 分发。新增检查只向唯一模拟管道读取 `noise.status`、`classisland.config.get`、`examaware.status`，采用合成状态和内存保存回调；未启动真实 Host、麦克风、录制设备探测或教学软件。真实首次设置、班级大屏与生产验收未运行；本轮不含推送、部署或发布。
