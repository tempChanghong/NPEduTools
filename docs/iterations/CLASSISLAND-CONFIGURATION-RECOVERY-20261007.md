# ClassIsland 配置请求顺序 · 2026-10-07

[迭代索引](README.md) · [使用指南](../GETTING-STARTED.md)

**交付：旧读取不再覆盖新路径，保存丢失回执后可通过新查询恢复配置。** 修复前使用真实 App 客户端和唯一模拟 Host 管道复现两处问题：旧“重新读取”在保存之后返回，将路径和版本改回旧值；保存回执丢失后，轮询虽收到新配置，界面仍沿用旧保存路径和版本。

| 情况 | 页面行为 |
| --- | --- |
| 保存或新“重新读取”开始 | 先前读取的响应和错误失效，不覆盖路径、版本、反馈及启动记录 |
| 正在保存或启动 | 后台查询暂缓；开始前已发出的查询不能随后覆盖操作结果 |
| 保存回执丢失 | 提示未确认；下次新查询核实实际保存结果，不自动重发保存 |
| 后台订阅实例变化 | 旧查询失效，重新读取配置；未确定的保存结果需重新核对 |
| 窗口关闭或正在退出 | 不再应用返回的配置查询 |

仅修改 App 的请求顺序和配置重新读取。Host 配置版本校验、启动策略、管理员工具、管理验证与 Windows 自启动规则不变。点击处理方法提取为可等待的任务，隔离测试直接调用同一实现。

## 验证

- 修复前失败证据：`.artifacts/launch-configuration/before/result.json` 与 `lost-before/result.json`。
- 6 组隔离 WPF 检查通过：旧读取晚于保存、旧读取错误晚于保存、两次读取倒序、旧轮询响应、旧轮询错误、保存丢失回执后新轮询恢复；核对路径、配置版本、反馈、记录和启动按钮，不自动重发操作。
- 19/19 项 `LaunchTests` 使用启动和课程接口替身的回归通过，0 跳过；上一轮 5 组管理员界面检查重新运行通过。
- App 与界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NPEduTools.Tests.LaunchTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/launch-configuration/wpf" --launch-configuration
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/launch-configuration/`，不随 Git 分发。请求只到唯一模拟管道，包含配置读写、执行记录查询及定时保护状态查询；模拟无保护状态，没有省略原有验证调用。未启动真实 Host、ClassIsland 或管理员工具，没有修改真实配置和 Windows 计划任务。后台重启及真实管理验证弹窗未实测；班级大屏、生产验收、推送、部署和发布均未进行。
