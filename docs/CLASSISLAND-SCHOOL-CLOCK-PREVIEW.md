# 第二步：现有预演全面采用 ClassIsland 时间

日期：2026-09-19。承接 [P0 桥接验证](CLASSISLAND-RECORDING-BRIDGE-P0.md)与[详细规划](CLASSISLAND-RECORDING-BRIDGE-PLAN.md)。

**本次把现有“今日计划与预演”接到真实学校时钟。** 今日日期、星期筛选、开始窗口、课后余量、今天暂停和事件时间均来自 ClassIsland。学校时钟异常时，不用 Windows 时间继续推算今天或启动任务。仍然只预演，不采集屏幕、声音或生成视频。

这里的“第二步”是对现有预演的时钟升级，并不代表详细规划的整个 P1、P2 均已完成：未来日期查询、周期/指定日期多条规则、17 项默认排除及正式录制协调器仍在后续范围。

## 1. 本次交付和使用

从主窗口“自动录课计划”或侧边栏“今日计划与预演”进入。顶部新增 ClassIsland 完整日期时间和时钟状态；显示的是收到的学校时间样本，没有以界面插值越过开始边界。

使用这套预演需要在 ClassIsland 中启用 P0 只读桥接插件 `npedutools.recordingbridge`，并重新启动本体。开发联调使用 `-epp` 外部插件目录；包的生成方法见 [P0 报告](CLASSISLAND-RECORDING-BRIDGE-P0.md)。本次只在临时本体副本加载插件，没有改动用户实际安装、课表或校时配置。插件管理器安装/升级/卸载验收尚待后续完成。

当前固定验证 ClassIsland 2.1.0.1，插件 0.1.0.0，桥接协议 v1。握手缺少必需能力、协议不符或本体版本未经验证时，学校时间不可用，预演等待；已有手动录制和其他工具不依赖这个桥接入口。

| 操作或情况 | 结果 |
| --- | --- |
| 启动预演 | 等待健康的学校时钟和同日生效课表，再判断课程窗口 |
| 今天不再预演 | 保存明确的学校日期，结束本节并暂停当日新任务 |
| 恢复今日预演 | 恢复尚未处理的课；已开始或已跳过的课程不重开 |
| 学校午夜自然跨日 | 日期和当天课表一起更新；上一日暂停不影响新一天 |
| 手动校时跨到另一日期 | 暂停新的开始，显示需要核对日期；活动预演仍受原期限约束，结束后关闭开关，用户核对后重新启动 |
| 同日时间回拨或前调 | 重新校验样本，重算待录计划；保留去重记录 |
| 关闭计划窗口 | 只隐藏；已启用预演继续检查 |
| 完整退出应用 | 停止预演；下次启动开关关闭，保留规则和去重记录 |
| 休眠/唤醒事件 | 停止预演、使时钟失效，要求核对后重新启用 |

“二分钟铃”仍解释为学校课表正式开始前固定两分钟；本次没有监听实际铃声，也没有接入自定义课前提醒事件。

## 2. ClassIsland Docs 与源码依据

沿用 P0 已核对的本地源码基线 `15273f82`、Docs 基线 `6b25407`，没有用其他版本的网络文档替换本地契约。

| 依据 | 本次使用方式 |
| --- | --- |
| [Docs：使用 IPC](classisland-docs-next/src/dev/ipc/ipc.md) | IpcClient、连接、CreateIpcProxy；Host 常驻工作进程持有一个连接 |
| [Docs：IPC 参考](classisland-docs-next/src/dev/ipc/reference.md) | 保留原课程状态接口供其他功能使用；预演不再通过公开倒计时反推学校日期 |
| [Docs：插件入口](classisland-docs-next/src/dev/plugins/plugin-base.md) | 沿用 P0 的插件注册与生命周期；实际 AppStopping 行为仍以本地源码验证为准 |
| [Docs：课程服务](classisland-docs-next/src/dev/lessons-service.md) | 生效课表选择归 ClassIsland；客户端不重写单双周、临时层算法 |
| [P0 桥接服务](../plugins/NPEduTools.ClassIsland.Bridge/BridgeService.cs) | IExactTimeService 有效时间、UI 线程一致采样、样本序号、单调缓存年龄、当日日程 |

新增能力是 NPEduTools 自己的适配与预演逻辑，不是宣称 ClassIsland Docs 已定义了 `classisland.school-clock`。

## 3. 数据链路与连接

```mermaid
flowchart LR
    CI[ClassIsland 有效时间与当前课表] --> P[P0 只读插件快照]
    P --> W[Host 隔离桥接进程]
    W --> H[Host 学校时钟缓存]
    H --> C[客户端新鲜度与连续性检查]
    C --> R[今日计划与预演]
    R --> UI[窗口和本地预演记录]
```

- 新增 Host 能力 `classisland.school-clock`，返回学校时钟、连接身份和同一快照的日程。旧 `classisland.schedule` 继续作为旧版只读诊断入口，已不供预演使用。
- Host 第一次收到该请求时启动一个桥接工作进程，约每 500 ms 从同一 IPC 连接拉取完整快照。App 约每 500 ms 读取 Host 缓存，一次只允许一个请求；日程/规则/事件没有变化时不重建表格。
- worker 使用 stdin 存活租约；Host 每 2 秒续租，8 秒无租约或管道关闭则 worker 自行退出。Host 退出也会主动清理所拥有的 worker。
- 连接最多等待 4 秒，单次代理有 2 秒生成接口超时及 3 秒外层等待，Host 收帧上限 8 秒；App 请求上限 3 秒。
- 失败后回收本次 worker，再按 1、2、4、8、15 秒上限退避重连。健康期间不会为每次界面查询创建新的 ClassIsland 连接。
- 同一 Host 的 App 查询共用缓存。换连接生成新的 ConnectionId；本体重启生成新的 BridgeInstanceId，旧样本不会当成新连接已就绪的证据。

当前沿用 P0 全量当日快照，每次最多 64 节、64 KiB；尚未实现按 revision 省略正文或降低插件日程散列频率。尚无整日内存趋势和大屏性能验收数据。

## 4. 学校日历与单调时间

学校日历时间决定日期、星期和课表边界；本进程 `Stopwatch` 只决定经过时长、超时、样本年龄和活动预演剩余上限。调度路径没有 `DateTime.Now`、`DateTime.Today` 或 `DateTimeOffset.Now` 回退。

桥接线上的学校时间仍是无时区的 `yyyy-MM-ddTHH:mm:ss.fffffff`。为了复用已有 `DaySchedule`、规则和记录类型，适配器在 NPEduTools 内部使用 offset=0 的 `DateTimeOffset` **作为学校日历数值容器，不代表真实 UTC**；不调用 `ToLocalTime` / `ToUniversalTime`，不再叠加学校偏移。外部诊断读取此字段也必须保留数字字段，不自动换算时区。本次 PowerShell 验证显式使用 `ConvertFrom-Json -DateKind String`，避免它默认把带偏移字符串转成本地时间。

样本年龄采用保守累加：插件缓存年龄 + 桥接请求往返耗时 + Host 缓存停留 + App 请求往返耗时 + App 缓存停留。各进程只计算自己的经过时长，不直接相减不同进程的 Stopwatch 计数。

| 健康检查 | 行为 |
| --- | --- |
| 初次连接、换连接、陈旧恢复或跳变 | 至少三个不同的有效样本、覆盖至少 500 ms，才允许开始 |
| 重复 sequence | 不增加稳定样本计数，不重置缓存年龄 |
| 新 sequence 但时间持续不前进 | 不视为成功预热；持续约两秒判为疑似冻结 |
| 累计样本年龄达到 3 秒 | 失效，不开始任务；界面标记最后时间已失效 |
| 相对单调经过时长偏差超过 2 秒，或 epoch 改变 | 重建校时锚点，保留课程去重记录 |
| 日程日期和学校时间不符、时段/身份无效 | 拒绝快照，等待后续有效读取 |
| 无课表、课表停用或课程计时停止 | 可继续显示学校时间；跟课表预演不开始 |

## 5. 活动预演的不可延长期限

例如学校课时 10:00–10:40，默认预演窗口为 09:58–10:45。只在收到健康学校样本已经进入窗口时开始；只剩课后余量不补开任务。

开始时建立单调期限：

```text
剩余上限 = max(0, 业务结束 − 学校时间样本 − 样本年龄上界)
单调截止 = 本地已过时长 + 剩余上限
```

此后期限只能提前，不能延后。有效学校样本已越过结束点即结束；校时前调或课表缩短可进一步收紧期限。回拨、冻结、断线、重连、将课时改长都不会重新获得完整剩余时长。

时间源异常期间无法知道后续铃声如何调整，只承诺按最后有效锚点的原剩余上限结束。事件无法得到新鲜学校时间时保存 `At=null`，界面显示“学校时间未知”，不伪造系统时间或精确停止时刻。

目前是 WPF 预演状态机，检查约 500 ms 一次；UI 卡住时只能在恢复后的检查中记录结束，不能作为真实录制停止采集的保证。正式采集还必须把调度、截止和控制方存活约束放入 Host/录制工作进程，见详细规划 P3。

## 6. 配置迁移

沿用 `%LocalAppData%/NPEduTools/ui/<管道哈希>.recording-preview.json`，格式升级到 v2。

- v1 的规则迁移到 v2；原文件备份为 `.recording-preview.json.v1.bak`。
- 旧系统日期的事件、课程发生标记、今天暂停及活动会话不冒充学校日期记录；保留在备份中，新账本重新开始，开关默认关闭，窗口提示核对。
- 若中途退出但备份已创建，确认备份与原文件相同后可重试；已有不同备份则保留文件并停止迁移。
- v2 的去重记录在应开始时生成，重启后不重复该课；未完成会话以学校时间未知记录中断。
- 文件损坏、未知版本、保存失败继续保留原有的停用和提示行为，读取上限 512 KiB。

## 7. 验证结果

| 验证层次 | 结果 |
| --- | --- |
| 锁定依赖还原、Release 构建、完整自动化测试 | 0 警告、0 错误，213 项通过，0 跳过；新增 22 项时钟与期限测试覆盖缓存重复、冻结、双向跳变、跨日、失联、期限收紧与迁移 |
| 真实 ClassIsland 2.1.0.1 + 插件 + Host | 23 项通过，其中 13 项 P0 场景与 10 项 Host 持续连接检查 |
| 真实 WPF + 私有桥接模拟端 | 学校日期设为 2031-04-07，验证学校日期显示、今天暂停、节次筛选、开始/停止、重启去重、断线、损坏配置与无采集进程 |

真实本体证据：

- [23 项结果](../.artifacts/classisland-bridge/e402831c78b5408cb68eb26bd2ccb427/summary.json)
- [Host 收到的学校时间与日程](../.artifacts/classisland-bridge/e402831c78b5408cb68eb26bd2ccb427/host-responses.json)
- [完整测试结果](../.artifacts/test-results/prototype.trx)
- [真实 WPF 测试结果](../.artifacts/auto-recording-ui/8858c761de1d40339d44fc4bd3f3af2e/summary.json)、[学校日期与事件截图](../.artifacts/auto-recording-ui/8858c761de1d40339d44fc4bd3f3af2e/preview-school-date.png)、[断线截图](../.artifacts/auto-recording-ui/8858c761de1d40339d44fc4bd3f3af2e/preview-disconnected.png)

本体实测包括：±120 秒校时且无额外时区偏移、末节无课间、课表停用、主计时器停止、UI 阻塞约 6.5 秒时样本失效且 IPC 仍响应、解除阻塞保持同一连接、学校跨午夜和星期课表同步切换、无课表、正常退出重连、强制终止重连、缺插件以及 Host 退出回收 worker。

未实测物理休眠/现代待机、大屏整课运行、管理员/跨会话权限组合、插件管理器升级卸载。当前结果也不等同于真实编码器在自动截止下的停止行为。

复现方式（构建完成、没有其他 ClassIsland 实例时）：

```powershell
./scripts/verify.ps1
./scripts/test-classisland-bridge.ps1 -VerifyHost
./scripts/test-auto-recording-ui.ps1
# Host 已启动时，只读查看当前缓存：
./src/NPEduTools.Cli/bin/Release/net10.0/NPEduTools.Cli.exe school-clock
```

真实本体脚本还要求先构建 P0 专用测试插件，步骤见 P0 报告。测试使用隔离目录和私有 NPEduTools 管道，不修改正在使用的课表与系统时间。

## 8. 代码索引与下一阶段

- [学校时钟契约](../src/NPEduTools.Contracts/SchoolClockFrame.cs)、[桥接适配器](../src/NPEduTools.Integrations.ClassIsland/ClassIslandSchoolClock.cs)、[Host 常驻监视器](../src/NPEduTools.Host/SchoolClockMonitor.cs)。
- [客户端时钟校验](../src/NPEduTools.Core/SchoolClockTracker.cs)、[预演与迁移](../src/NPEduTools.Core/RecordingPreview.cs)、[计划窗口](../src/NPEduTools.App/AutoRecordingWindow.xaml.cs)。
- [时钟测试](../tests/NPEduTools.Tests/SchoolClockTests.cs)、[截止与日期测试](../tests/NPEduTools.Tests/SchoolPreviewDeadlineTests.cs)。

下一阶段补齐有限未来日期查询，再实现周期性/指定日期计划及 17 项默认排除、日期覆盖与冲突预览。完成这些预演验证后，才进入 Host 统一录制协调、真实采集截止与目标大屏实测。
