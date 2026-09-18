# 自动录课：今日计划与预演

日期：2026-09-18。当前交付为自动录课的第一阶段：读取日程、配置计划、模拟调度。**预演不会采集屏幕或声音，不会生成视频。** 已有的独立手动录制继续使用原入口。

完整目标见 [设计文档](AUTO-LESSON-RECORDING-DESIGN.md)。本阶段没有修改 ClassIsland 本体、插件或用户课表，也不需要安装新插件。

## 使用方法

1. 运行 `./scripts/start-app.ps1`，启动并配置好 ClassIsland。
2. 点击主窗口“自动录课计划”，或侧边栏录制区的“今日计划与预演”。
3. 查看生效课表、正式上课时间、计划窗口及筛选/冲突原因。
4. 配置星期、科目、节次、课前提前量和课后余量，点击“应用并保存规则”。节次留空表示全部；可输入 `1,3,5`。星期、科目、节次条件须同时满足。
5. 点击“启动预演”。事件列表会出现“应开始”“应停止”，方便先核对整天的调度行为。

默认所有星期、所有科目、所有节次；提前 2 分钟、延后 5 分钟。前后余量分别可选 0～5 分钟，课后不能超过 5 分钟。

科目按 ID 保存，重名科目不会自动合并。列表来自今天的课表；已选择但今天未出现的 ID 会保留。目前没有多条规则、任意时间段筛选或跨日历的科目选择器。

“二分钟铃”目前解释为**正式上课前固定两分钟**，不是监听扬声器声音，也不跟随 ClassIsland 自定义课前提醒。现有公开 IPC 未提供可直接使用的该提醒事件；若以后要跟随实际提醒，需要另行接入。

| 操作 | 结果 |
| --- | --- |
| 选择某节并点击“跳过 / 结束选中节” | 未开始则跳过；正在预演则结束，本次课程不会重复模拟开始 |
| 今天不再预演 | 结束当前预演，暂停当天的新任务 |
| 恢复今日预演 | 恢复未处理课程；已经开始或跳过的课程仍不重复 |
| 停止预演 | 停止调度及当前模拟会话；再次启用也不重复本节 |
| 关闭或隐藏计划窗口 | 已启用的预演在 NPEduTools 后台继续运行 |
| 完整退出 NPEduTools | 停止预演；下次启动保留规则和记录，但预演开关默认关闭 |

这版没有清除单节完成标记的界面。预演会保留最多 512 个课程发生记录、200 条事件。不要把预演记录当作视频录制成功凭据。

## 时间窗口与边界行为

例如 10:00–10:40 的课，默认窗口为 09:58–10:45。10:40 进入课后余量，不立即结束；用户可提前结束，到 10:45 后的下一次调度检查结束预演。界面计时器每秒检查，不承诺毫秒级触发。

| 情况 | 当前实现 |
| --- | --- |
| 课中才启动预演 | 有新鲜、有效日程时允许从当前时刻模拟开始，标记缺失开头 |
| 仅剩课后余量 | 不新建任务，避免只模拟录到课间 |
| ClassIsland 断开或数据过期 | 不开始新任务；当前模拟会话仍按已保存的期限结束 |
| 课表禁用或档案切换 | 有新鲜快照证实后，结束当前模拟会话 |
| 临时换课、修改筛选规则 | 重算待开始计划；当前课不再符合规则时结束；可缩短当前截止时间，不延长 |
| 下一节紧接着开始 | 缩短上一节课后余量，为下一节留出提前窗口；未选下一节也不侵入其正式课时 |
| 正式课时重叠，或提前窗口进入上一节正式课时 | 显示冲突，不启动该冲突任务；可调整提前量或课程选择 |
| 同科目不同节次 | 按具体日期和正式时间段区分，不用科目名称去重 |
| 临时层换 ID / 换科目 | 同档案下正式时间段重叠的发生实例沿用完成标记，避免重开 |
| 系统时间回拨超过 2 秒 | 停止并关闭预演，需要重新启用 |
| 时间前跳、休眠后唤醒 | 下一次检查结束已到期的会话，不补发已经完全过期的课程 |

课程发生记录在“应开始”发出时就生成并保存；用户停止、重连或重启不会重放这节。这个保守策略也会阻止同一档案下后来创建的、与已处理正式课时重叠的新任务。

当前依赖 NPEduTools 进程运行，不会唤醒已退出的应用或休眠电脑。跨午夜课程暂不支持；深夜的跨日边界与复杂调课仍需后续专项验证。

## 日程来源和时间校验

链路为：

```mermaid
flowchart LR
    A[ClassIsland 公开 IPC] --> B[隔离日程读取进程]
    B --> C[Host: classisland.schedule]
    C --> D[规则生成今日计划]
    D --> E[预演状态机]
    E --> F[界面与本地事件记录]
```

从 `IPublicLessonsService.CurrentClassPlan` 与 `IPublicProfileService.Profile` 读取当前实际生效的课表、时间表和科目，不自行复制 ClassIsland 的单双周与临时层选择算法。只将 `TimeType == 0` 的上课时间点映射到课程，节次不使用包含课间的 `CurrentSelectedIndex`。

通过 `DaySchedule` 返回档案、课表、时间表 ID、日期、采样时间、修订摘要、是否启用、时间校验结果及课程列表。停用课程和已不存在的科目仍可显示，但不会进入预演计划。时间无效、课时与课程数量不匹配、重复且无法唯一识别的生效课表会明确返回不可用。

读取前后复核生效课表、档案内容摘要与启用状态，发现变化就拒绝本次快照，等待下一次刷新。多个 IPC getter 本身不是事务，因此这只是减少混合读取，不是上游原子快照保证。

真实本体联调发现，IPC 反序列化可能经 `EditingSubjects` 创建带随机 ID 的科目副本。直接散列整个 Profile 会误判“每次都在变化”。现在只散列影响计划的字段及被课程/默认科目引用的科目 ID，排除运行期缓存和自动生成的时间戳。

窗口可见或预演开启时，在一次读取结束约 5 秒后尝试下一次读取；不并发发起查询。新任务要求快照不超过 15 秒、日期为本机今天，并通过时间校验。每次请求有 10 秒 Host 期限和 13 秒客户端取消期限；使用独立工作进程处理可能阻塞的上游 getter。

时间校验使用下一次上课/课间时间和 ClassIsland 倒计时反推当前时刻，与读取前后的 Windows 本机时间比较，容许 2 秒误差。缺少可用倒计时、最后一节没有下一课间、明显校时偏差时，会显示计划但不启动新的预演任务。已开始的模拟会话继续受原截止时间约束。

**这不是完整的 ClassIsland 精确时间接口。** 日程日期仍来自本机；整日偏移或特殊调试时间等情况没有获得权威的绝对日期校验。接入真实自动采集前，应补齐统一时间基准和目标大屏验证。当前不会声称所有 ClassIsland 校时/调试配置均已兼容。

每日日程最多 64 节；内部规范化档案摘要输入限制为约 2 百万字符，输出使用原协议的 64 KiB 帧限制。科目显示名最多 60 字符、课表显示名最多 80 字符；不把完整档案发送到界面。

## 本地保存与故障行为

规则、事件与课程标记保存在：

```text
%LocalAppData%/NPEduTools/ui/<当前 Host 管道的哈希>.recording-preview.json
```

格式版本为 1。写入临时文件后替换，避免直接截断原记录；读取限制 512 KiB。记录损坏或版本不支持时保留原文件，禁止启动预演和修改规则，仍允许查看课表。写入失败会停止预演并提示检查权限、空间。排查后可在完全退出程序时备份并移走该文件，以重新配置；这同时清除此前的课程去重记录。

预演状态机没有录制器或启动进程的依赖。本阶段不接管正在运行的手动录制，预演状态也不用于判断手动录制是否成功。

## CLI 与验证

Host 已运行时，可查询同一日程接口：

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' schedule --timeout-ms 10000
```

复现构建与测试：

```powershell
./scripts/verify.ps1
./scripts/test-classisland-live.ps1 -Schedule
./scripts/test-auto-recording-ui.ps1 -Configuration Release
./scripts/test-app-smoke.ps1 -Configuration Release
./scripts/test-recording-ui.ps1 -Configuration Release
```

真实 ClassIsland 测试使用本地开发构建的隔离副本和临时课表；已有实例运行时脚本拒绝启动，避免干扰。WPF 测试使用私有模拟 IPC 管道和独立配置，不修改用户的 ClassIsland 档案。

本次验证结果：

| 验证 | 结果与本机证据 |
| --- | --- |
| 锁定依赖还原、Release 构建、自动化测试 | 0 警告、0 错误；186 项通过、0 失败：[TRX](../.artifacts/test-results/prototype.trx) |
| 真实 ClassIsland 公开 Profile IPC | 两节课正确映射、课间不计入节次、倒计时校验通过：[摘要](../.artifacts/classisland-live/f0ade1677a1341ef9a13fbdcee9d20df/summary.json) |
| WPF 今日计划与预演 | 规则筛选、模拟开始/停止、手动跳过、刷新及完整重启去重、侧边栏入口、断线提示、无效配置保留全部通过：[摘要](../.artifacts/auto-recording-ui/16f0e2891eac4952a9812832cc90c7dc/summary.json) |
| 现有主窗口与后台生命周期 | 侧栏拖动、退出、重连与启动偏好等通过：[摘要](../.artifacts/app-smoke/8787e99b6f554c19868e54811d4b48cd/summary.json) |
| 现有手动录制 | WPF 开始、侧栏暂停/继续/停止、退出保存均通过，两份 MP4 完整解码成功：[摘要](../.artifacts/recording-ui/13ca1563b57b4d12803afbcfe8b1eb26/summary.json) |

预演引擎测试还覆盖默认前 2 / 后 5 分钟、窗口冲突、星期/科目/节次条件、断线后固定期限、课中补录、仅余量时不补录、规则变更、时间回拨、临时层去重，以及同名科目的不同发生实例。IPC 测试覆盖时间不一致、临时换科目、已删除科目和缺失本体。

可查看 [预演运行截图](../.artifacts/auto-recording-ui/16f0e2891eac4952a9812832cc90c7dc/preview-running.png)、[断线提示截图](../.artifacts/auto-recording-ui/16f0e2891eac4952a9812832cc90c7dc/preview-disconnected.png)、[无效配置截图](../.artifacts/auto-recording-ui/16f0e2891eac4952a9812832cc90c7dc/preview-invalid-config.png)。这些 `.artifacts` 证据保留在本机，不提交到 Git；其他机器可用上述脚本重新生成。

## 后续接入真实录制

下一阶段需要让自动任务持有独立的录制会话身份和绝对截止时间，并在录制工作进程中执行结束约束，不能仅由 WPF 定时器负责停止采集。还要处理手动录制占用、暂停不延长、退出保存、设备失败及真实首末帧时间。

本阶段未实现跟随实际二分钟铃、多条规则、每课独立预设、连续采集后分段导出，以及自动录课整课稳定性验收。现有 i7-1065G7 大屏的现场测试仍是接入采集后需要完成的工作。

## 代码索引

- [日程契约](../src/NPEduTools.Contracts/ScheduleModels.cs)、[IPC 日程适配器](../src/NPEduTools.Integrations.ClassIsland/ClassIslandScheduleProbe.cs)、[严格 Profile 代理](../src/NPEduTools.Integrations.ClassIsland/StrictProfileShape.cs)。
- [计划规则与冲突处理](../src/NPEduTools.Core/RecordingPlanner.cs)、[预演与持久化](../src/NPEduTools.Core/RecordingPreview.cs)。
- [WPF 计划窗口](../src/NPEduTools.App/AutoRecordingWindow.xaml)、[窗口逻辑](../src/NPEduTools.App/AutoRecordingWindow.xaml.cs)。
- [计划和状态机测试](../tests/NPEduTools.Tests/RecordingPlannerTests.cs)、[日程 IPC 测试](../tests/NPEduTools.Tests/ScheduleIntegrationTests.cs)。
- 本地参考：[课程服务](classisland-docs-next/src/dev/lessons-service.md)、[IPC 参考](classisland-docs-next/src/dev/ipc/reference.md)、[课表与临时层](classisland-docs-next/src/app/profile/classplan.md)。
