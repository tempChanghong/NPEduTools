# 考试看板管理页协议恢复 · 2026-10-07

[迭代索引](README.md) · [测试入口](../TESTING.md)

**交付：管理页收到无效状态、操作或配对回执后显示未知状态，并通过后续只读查询恢复。** 修复前，模拟后台返回请求编号错误、版本错误、零长度帧或空消息：状态轮询退出并保留旧连接；方案操作和配对导出抛出未处理的协议异常。

本轮只将 InvalidDataException 纳入三条路径的现有异常处理。保留协议校验、配置修订和查询时序规则。当前回执失效时清除旧的可操作方案，连接、自启动及放映状态显示未知；操作解除等待并提示核实。配对导出不写入无效回执，也不自动重试。后续有效查询恢复显示与操作按钮，保留用户正在编辑的程序路径。过期查询的异常仍不能覆盖较新的操作结果。

## 验证

- 修复前证据：.artifacts/examaware-protocol-recovery/before/result.json，12 种路径／错误组合全部复现失败。
- 12 组新增隔离 WPF 检查通过：状态查询、方案操作、配对导出分别覆盖请求编号错误、版本错误、零长度帧和空消息；验证未知状态、禁用旧方案、解除等待、保留既有合成文件和重新查询恢复。
- 另补 2 组旧查询异常回归：新方案成功后，迟到的旧无效查询不清除新方案；随后有效查询更新放映状态，当前查询断连仍显示未知。
- 13 组已有管理页／快捷入口回归通过；本次入口合计 27 组，覆盖正常接受、拒绝、关闭时取消、路径编辑与配对文件写入失败。
- 70/70 项 ExamAware 及协议测试通过，0 跳过。界面测试项目 Release 构建通过，0 警告、0 错误。
- 已查看管理页未知状态和恢复状态截图，位于 .artifacts/examaware-protocol-recovery/wpf。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ExamAwareTests|FullyQualifiedName~ProtocolTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/examaware-protocol-recovery/wpf" --examaware-status
node scripts/check-doc-links.mjs
```

使用唯一模拟管道、合成考试状态和自己的临时文件，结束后清理。没有启动真实 ExamAware、Host、管理员工具或文件对话框，没有放映、修改 Windows 自启动或真实配对。真实软件、教室设备及生产验收未运行；本轮未推送、部署或发布。
