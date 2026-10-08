# Host 无效数据恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

目标：修复三个已经复现的异常处理遗漏。基线为 `f0d39cf`（PR #12 合并后的 main），仅本地提交。

| 问题 | 修复前 | 修复后 |
| --- | --- | --- |
| ExamAware 配置为 `null`、缺字段或版本不支持 | 内容校验抛出异常，Host 构造中断 | 桥接显示不可用，拒绝配对；原文件保留 |
| 课堂模式记录内容无效或考试状态未暂停录课 | 内容校验抛出异常，Host 构造中断 | 模式显示不可用，自动录课保持暂停；原文件保留 |
| 录制器返回 `null` 或超过 16384 字符的状态行 | 读取任务失败，异常再次从清理过程抛出 | 启动失败有明确状态，清理可完成 |

三个执行边界都漏接了 `InvalidDataException`；它不属于 `IOException`。生产代码仅在对应 catch 中补上该类型，不更改数据格式、覆盖损坏文件或自动重试录制。

## 复现与验证

- 修复前：11 个针对场景中 8 个失败，3 个普通坏 JSON 对照通过。
- 修复后：111 项相关回归通过，0 跳过；包含两个独立测试 Host 的启动、模块状态、`host.ping` 和原文件保留检查。
- 录制器使用测试进程输出合成状态；未调用真实录制器或音频设备。
- Release 串行构建：0 警告、0 错误。
- 全量测试：822 项桌面测试 + 183 项 NPEP 测试，共 1005 项通过，0 失败、0 跳过。
- 文档检查：199 份 Markdown、931 个本地文件链接通过；差异检查通过。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore --configuration Release '-m:1' --filter 'FullyQualifiedName~ExamAwareTests|FullyQualifiedName~ClassroomModeTests|FullyQualifiedName~RecorderProcessTests|FullyQualifiedName~RecordingExecutionTests|FullyQualifiedName~HostReadinessTests'
./scripts/dotnet.ps1 build NPEduTools.sln --no-restore --configuration Release '-m:1'
./scripts/dotnet.ps1 test NPEduTools.sln --no-build --no-restore --configuration Release
```

本地证据：`.artifacts/overnight-20261008/` 下的 before、after、integration、full 测试报告。真实大屏、录课设备、部署和发行未测试。

## 后续：录制器启动握手

基线 `717116e`。Host 原先在收到任何可解析的 JSON 后都放行启动，即使它是空对象、缺少提示文字、未知状态、带会话归属的 Idle，或旧 Saved 回执。这些内容不能证明新组件已准备好接收启动命令，可能让页面停在“正在准备录制”。

现在首条状态必须是未占用的 Idle，或未占用且明确报告的 Failed，并带有提示文字；否则启动失败并正常清理进程。正常 Idle 后的启动／停止命令、会话匹配和组件原始错误仍按原流程处理，不修改协议或录制设置。

- 修复前：10 项测试中，5 个无效握手用例失败（启动错误地返回成功），5 个对照通过。
- 修复后：19 项录制进程、执行记录和 Host 就绪检查通过，0 跳过；测试进程仅收发合成状态，不采集音视频。
- Release 串行构建：0 警告、0 错误。
- 全量测试：829 项桌面测试 + 183 项 NPEP 测试，共 1012 项通过，0 失败、0 跳过。文档链接与差异检查通过。

本轮证据位于 `.artifacts/recorder-handshake-20261008/`，保留 before、after 和 full 报告。真实录课设备、部署和发行未测试。
