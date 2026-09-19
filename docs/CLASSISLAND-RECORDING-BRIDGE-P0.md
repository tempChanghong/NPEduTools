# ClassIsland 时间桥接：P0 可行性验证报告

日期：2026-09-19。结论：**最小只读桥接插件可在本地 ClassIsland 2.1.0.1 中加载，并通过本体 IPC 向 .NET 10 外部程序提供有效学校时间与当天课表。P0 可进入下一阶段。**

本轮实现了 [详细规划](CLASSISLAND-RECORDING-BRIDGE-PLAN.md) 的第一步。未修改 ClassIsland 源码、系统时间、用户课表或录制器；未把桥接接入现有预演窗口，也未开启自动采集。所有校时、无课、卡顿、退出操作均发生在隔离的本体副本与临时配置中。

后续更新：现有预演现已接入桥接，见[第二步实施报告](CLASSISLAND-SCHOOL-CLOCK-PREVIEW.md)。本文保留 P0 验证时点的范围与证据。

## 1. 实际交付

| 产物 | 用途 |
| --- | --- |
| [桥接插件](../plugins/NPEduTools.ClassIsland.Bridge/Plugin.cs)与[桥接服务](../plugins/NPEduTools.ClassIsland.Bridge/BridgeService.cs) | net8.0，只读采样 ClassIsland 时间与当日日程 |
| [独立契约](../src/NPEduTools.ClassIsland.Bridge.Contracts/BridgeProtocol.cs) | IRecordingBridgeP0、扁平 JSON 快照、时钟变化观测 |
| [外部探测程序](../tools/NPEduTools.BridgeProbe/Program.cs) | net10.0，握手、读取、超时和缺失插件诊断 |
| [真实本体测试脚本](../scripts/test-classisland-bridge.ps1) | 创建隔离副本、运行 13 项检查、保存快照与日志、回收本次进程 |
| [专用测试插件](../tests/NPEduTools.Bridge.TestFixture/Plugin.cs) | 仅操作临时本体的公开设置/服务，模拟偏移、卡顿及退出；不进入交付插件 |
| [时钟单元测试](../tests/NPEduTools.Tests/BridgeClockTests.cs) | 任意偏移、前后跳变、冻结、UTF-8 大小限制 |
| [打包脚本](../scripts/package-classisland-bridge.ps1) | 调用固定 PluginSdk 打包，核对包内容并生成 SHA-256 |

本机验证包：[NPEduTools.ClassIsland.Bridge.cipx](../.artifacts/bridge-package/8983bea7cc414f16a6a70c6b44074f64/NPEduTools.ClassIsland.Bridge.cipx)。

包 SHA-256：`C987B79F143F0A3E59EE52BF5E5199A7C3AA42A930EBCDB6496D4BE5CA8A18AB`。包内容与哈希见 [package-verification.json](../.artifacts/bridge-package/8983bea7cc414f16a6a70c6b44074f64/package-verification.json)。这是 P0 验证包，不是已经接好自动录制的正式产品包；尚未验证通过插件管理器安装/升级/卸载的完整流程。

## 2. ClassIsland Docs 的落实

| Docs 依据 | 本次实现与核实 |
| --- | --- |
| [插件入口](classisland-docs-next/src/dev/plugins/plugin-base.md)、[依赖注入](classisland-docs-next/src/dev/basics/dependency-injection.md) | Initialize 注册 BridgeService，AppStarted 显式解析并启动，不提前解析容器 |
| [事件与生命周期](classisland-docs-next/src/dev/events.md) | PostMainTimerTicked 后采样；AppStopping 同步取消订阅与定时器；不等待 IPC 发送 |
| [插件基础与程序集隔离](classisland-docs-next/src/dev/plugins/basics.md) | 不复制宿主 Core、Avalonia、IPC、Newtonsoft 运行库；契约在插件加载上下文中成功注册 |
| [IPC 用法](classisland-docs-next/src/dev/ipc/ipc.md)、[IPC 参考](classisland-docs-next/src/dev/ipc/reference.md) | 复用 IIpcService.IpcProvider 注册新的只读服务，外部使用 IpcClient/代理；不替换官方服务 |
| [课程服务](classisland-docs-next/src/dev/lessons-service.md) | 读取实际 CurrentClassPlan，与日期查询返回的对象及 ID 对照；课间不计入课程节次 |
| [创建项目](classisland-docs-next/src/dev/plugins/create-project.md)、[发布插件](classisland-docs-next/src/dev/plugins/publishing.md) | 固定 SDK 2.1.0.1，提供独立 manifest；用 SDK CreateCipx 目标产生包及原生校验摘要 |

Docs 本地提交仍为 `6b25407`，ClassIsland 源码提交为 `15273f82`。PluginSdk 2.1.0.1 的包元数据指向相同 ClassIsland 提交。两边契约保持 net8.0 兼容，NPEduTools 探测程序为 net10.0；未更改 ClassIsland 的 SDK 配置。

延续规划中的源码差异处理：当前 PluginBase 没有 OnShutdown，实际使用 AppStopping。对共享 IpcProvider 只注册服务，不重复 StartServer，也不 Dispose 本体 provider。

## 3. 已验证的架构选择

**采用本体现有 IPC 上的新只读服务，P0 不需要增加独立命名管道。** 服务契约名为 `IRecordingBridgeP0`，握手标识为 `npedutools.recordingbridge.p0`，明确与未来正式调度协议隔离。

仅暴露两个异步方法：

```text
GetHelloAsync()     → 协议、插件/宿主版本、实例 ID、能力与生命周期
GetSnapshotAsync()  → 有效学校日期时间、采样年龄、序号、时钟状态和当日课程
```

没有修改时间、编辑课表、启动录制、执行命令或停止本体的桥接方法。测试中修改校时的是另一个只在隔离目录加载的 TestFixture 插件，验证包不含该程序集。

ClassIsland 返回的学校时间格式为无 UTC 后缀的本地日期时间字符串，不拿它冒充真实 UTC，也不和系统当前日期拼接。学校日期、星期和当日课表全部来自该时间。采样年龄通过本进程 Stopwatch 计算；系统日历变化不参与过期判断。

时钟每隔至少 250 ms 采样一次。在课程计时器停止时使用 UI Dispatcher 定时器继续读取 IExactTimeService，明确返回 TimerStopped 的日程状态。无课返回 NoPlan、停用课表返回 Disabled；两者都可以同时提供有效时间。

快照包含 bridgeInstanceId、sequence、clockEpoch、effectiveLocalDateTime、sampleAgeMs、clockState、lessonTimerRunning、日程身份和课程数组。读取旧缓存不会增加 sequence。缓存年龄超过 3 秒时返回 Stale；时间回拨或相对经过时间显著跳变会增加 epoch，并经过稳定样本重新进入 Advancing。

UI 线程中只访问本体模型并发布副本；IPC 请求读取已经发布的副本，不等待 UI 线程。P0 为验证方便，每个采样周期仍复制并散列有限的当日日程，尚未做“时钟与日程分频缓存”优化。未来阶段需要落实该优化并测量性能，不以本轮两节课程的结果代替大档案压力测试。

每日日程最多 64 节，响应限制 64 KiB UTF-8；不发送完整 Profile。无有效课表 ID、课程映射异常、跨午夜课程条目等情况返回 Unavailable/错误，不返回默认时间冒充成功。前后采样跨日或源对象发生替换时，本次拒绝发布正常日程。

## 4. 真实本体联调结果

最终完整通过的证据目录：

```text
.artifacts/classisland-bridge/acee16f9a94343f8abdc3db6cbdcaa9d/
```

- [13 项检查摘要](../.artifacts/classisland-bridge/acee16f9a94343f8abdc3db6cbdcaa9d/summary.json)
- [完整读取结果](../.artifacts/classisland-bridge/acee16f9a94343f8abdc3db6cbdcaa9d/responses.json)

| 检查 | 结果 |
| --- | --- |
| 真本体加载、共享 IPC、net8→net10、两节课程映射 | 通过，返回实际宿主版本 2.1.0.1 |
| 运行中手动偏移 +120 秒 | 通过，学校时间跟随，并产生新的 clockEpoch |
| 运行中手动偏移 −120 秒 | 通过，回拨被识别，稳定后恢复 Advancing |
| 最后一节后没有课间时间点 | 通过，仍可直接取得学校时间，不依赖下一课间倒计时 |
| 禁用课表 | 通过，Disabled 与学校时间同时返回 |
| 停止课程主计时器 | 通过，UI 时钟采样继续，日程显示 TimerStopped |
| UI 阻塞 6.5 秒 | 通过，IPC 仍能返回旧快照；年龄持续增长并进入 Stale |
| 学校时间自然跨午夜 | 通过，日期及星期课表一起切换；没有修改 Windows 时间 |
| 清空临时档案课表 | 通过，NoPlan 且学校时间仍前进 |
| 正常退出与离线查询 | 通过，日志确认取消订阅；离线查询有界失败 |
| 正常退出后重启 | 通过，新查询成功且 bridgeInstanceId 改变 |
| 强制终止后重启 | 通过，离线查询失败，重启后是新实例 |
| 本体正常启动但未加载桥接 | 通过，服务缺失有界失败，不返回系统时间 |

UI 阻塞期间，实际连续读到相同 sequence `26`，sampleAgeMs 从约 `3898` 增长至 `6520`；没有因为 IPC 请求成功而伪造新的采样。正常退出的 stderr 中观察到 `NPEduTools bridge stopped; subscriptions released.`。

偏移测试通过临时本体的公开 Settings.TimeOffsetSeconds 属性完成。跨日测试把学校时间设置到午夜前约五秒，先确认午夜前快照，再观察自然跨日与新课表 ID。测试机器的 Windows 时间未修改，NTP 在临时配置中关闭以使测试可重复。

“重连”在本阶段指本体重启后，外部诊断客户端能够建立新连接并读取新实例；**不是已经实现 Host 的常驻桥接连接恢复**。当前 Probe 每次查询运行一个有硬超时的短进程，该生产化常驻连接属于 P1。

## 5. 构建与单元测试

完整执行 `./scripts/verify.ps1`：锁定依赖还原成功，Release 构建 **0 警告、0 错误**，**191 项测试通过、0 失败**。[测试记录](../.artifacts/test-results/prototype.trx)

新增 5 个测试用例覆盖学校时间任意偏移/跨日、正向及反向跳变、时间冻结和响应字节上限。冻结状态机有单元测试；真实本体测试覆盖的是 UI 阻塞造成采样陈旧，**尚未在真实 NTP 同步过程中诱发并验证 ExactTimeService 内部冻结**。

独立测试插件最终也以 0 警告、0 错误构建。它通过 ExternallyResolved 的本地引用使用公开 SettingsService，避免构建工具递归扫描整个本体输出而引入不必要的版本冲突。正式桥接只使用公开抽象服务，不需要引用本体 ClassIsland.dll。

插件输出及验证包经检查不含 ClassIsland.Core、Avalonia、dotnetCampus.Ipc、Newtonsoft.Json 的另一份运行库，也不含 TestFixture；包中包含 manifest、桥接 DLL、契约 DLL、README 和项目 LICENSE。SDK 输出的 MD5 与脚本额外生成的 SHA-256 都保留在包证据目录。

## 6. 复现流程

先保留现有本地 ClassIsland Debug 构建，退出正在运行的 ClassIsland，再从 NPEduTools 根目录执行：

```powershell
./scripts/verify.ps1
./scripts/dotnet.ps1 restore tests/NPEduTools.Bridge.TestFixture --locked-mode
./scripts/dotnet.ps1 build tests/NPEduTools.Bridge.TestFixture -c Release --no-restore
./scripts/test-classisland-bridge.ps1
./scripts/package-classisland-bridge.ps1
```

测试插件默认引用：

```text
D:/WebstormProjects/ClassIsland/ClassIsland.Desktop/bin/Debug/net8.0-windows10.0.19041.0/ClassIsland.dll
```

如果目录不同，构建测试插件时传入 `-p:ClassIslandBinaryDirectory=<本体输出目录>`，运行联调脚本时传入 `-ClassIslandBinary <本体可执行文件>`。只承诺上述已验证版本，不能把任意其他本体的输出混用。

测试脚本检查已有本体与全局互斥锁；有实例运行就明确退出，不结束用户进程。测试副本、配置和控制文件均位于本次随机 `.artifacts/classisland-bridge/<id>` 目录，finally 只回收脚本创建的进程。测试插件本身还要求显式测试目录与 fixture-marker 才能加载。

如已在开发本体加载验证桥接，可独立读取：

```powershell
./scripts/dotnet.ps1 run --project tools/NPEduTools.BridgeProbe -c Release --no-build --no-restore '--' hello
./scripts/dotnet.ps1 run --project tools/NPEduTools.BridgeProbe -c Release --no-build --no-restore
```

Probe 输出 JSON；成功退出码 0，参数错误 2，连接/协议异常 3，最终进程硬超时 4。有效性还要看生命周期、clockState、采样年龄和 day.status，不能只看进程退出码。

## 7. 本阶段的边界与下一步

P0 已解决原先最重要的疑问：插件加载与程序集身份兼容、从 IExactTimeService 获取完整时间、复用现有 IPC，以及在无课/末节/跨日情况下读取时间。这条路线可以保留，不需要修改 ClassIsland 核心或新建另一套桥接管道。

以下内容仍属于后续阶段：

- 常驻 Host 适配器、能力协商完善、乱序/重连恢复、严格的权威时钟状态机。
- 日程缓存分频、未来日期查询、临时课表群/临时层复杂映射和大档案压力测试。
- UI 显示学校时钟，现有预演从系统时间迁移到学校时间。
- 周期/指定日期规则、17 项默认排除与覆盖、执行账本迁移。
- 自动录制器的所有权、独立截止与控制租约；手动/自动仲裁。
- 管理员权限组合、多用户会话、NTP 内部冻结、休眠恢复及真实大屏整课测试。
- 插件管理器安装/更新/卸载和正式分发兼容性矩阵。

因此下一步应进入 **P1：把桥接数据变成 NPEduTools 可长期依赖的学校时钟与日程来源**，随后才升级规则预演和接入真实录制。现有主程序仍运行此前的预演逻辑，本轮不会在用户课堂中自动触发采集。

