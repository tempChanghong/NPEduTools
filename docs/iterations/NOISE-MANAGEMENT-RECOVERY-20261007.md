# 定时监测管理验证恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：管理设置查询和口令提交收到无效回执时，界面保留失败提示与手动重试机会。** 修复前，唯一模拟后台分别返回请求编号错误、版本错误、零长度帧或空消息，协议异常逃出入口与弹窗的真实界面事件。

本轮只将 InvalidDataException 纳入两处现有错误处理。协议校验、口令规则、授权范围与 Host 验证保持不变；无效设置查询不打开管理弹窗，无效提交不完成弹窗、不接受回执中的授权票据。提交按钮恢复可用，显示“未继续操作”。用户重新提交后使用新的请求编号，收到有效回执才按原规则完成；没有自动重发。

## 验证

- 修复前证据：.artifacts/noise-management-recovery/before/result.json，12 种路径／错误组合全部复现未处理事件异常。
- 12 组新增隔离 WPF 检查通过：口令配置、管理授权、设置入口分别覆盖请求编号错误、版本错误、零长度帧和空消息。
- 配置／授权检查使用真实模态弹窗和提交按钮，验证失败不关闭、不接受票据、按钮恢复及显式重试成功；授权请求保留操作、目标请求、后台实例和监测会话范围。关闭弹窗后合成口令输入清空。
- 入口检查验证无效状态不打开弹窗，手动重试只发送新的管理状态查询。16 组已有监测回归通过，本次入口合计 28 组。
- 28/28 项管理保护、模拟管道及协议测试通过，0 跳过。界面测试项目 Release 构建通过，0 警告、0 错误。
- 已查看配置与授权弹窗的失败提示截图，位于 .artifacts/noise-management-recovery/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NoiseManagementTests|FullyQualifiedName~NoisePipeTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/noise-management-recovery/wpf" --noise-status
node scripts/check-doc-links.mjs
```

仅使用唯一模拟管道、合成口令、状态和票据；模拟服务不会存储口令或执行授权后的操作。没有修改真实管理设置、采集麦克风、停止定时监测、运行真实 Host 或连接学校服务。真实设备及生产验收未运行；本轮未推送、部署或发布。
