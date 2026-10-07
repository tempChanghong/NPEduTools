# NPEP N3 首批实现：独立执行内核与录制保留权

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

后续进展：见 [2026-09-26 本机适配与协调接入](NPEP-N3-LOCAL-ADAPTER-20260926.md)。下文实现状态和测试数字属于 2026-09-25，不代表后续代码已通过测试。

日期：2026-09-25。工作目录：`D:/CodeProjects/NPEduTools`。

**状态：N3.0 契约审阅草案与 N3.1 执行内核已落地；N3 整体尚未完成，不能从网页远程切换。** 本次没有连接生产设备、启动或退出真实 ClassIsland / ExamAware2、修改 Windows 自启动、推送或部署。现有 N2 通知版本继续使用原发布包。

## 已实现的内容

### 独立执行顺序

`RemoteExamExecutor` 是 Host 内部执行内核，输入只有操作 ID、预期运行修订和原课堂模式修订，不接收可执行路径、自启动开关、任意命令名或远程“已保存考试内容”声明。

1. 获得独占运行保留权，检查去重、修订、未解决记录和容量。
2. 保存检查记录，核验授权并原子保留空闲录制器。
3. 通过适配器只读检查桌面、通知、配置与旧模式恢复状态。
4. 在首次副作用前核验开始许可，持久保存独立 N3 暂停。
5. 目标尚未就绪才准备 ExamAware2；核实就绪后才请求正常退出 ClassIsland。
6. 动作派发前再次检查授权与配置；适配器必须在 UAC 等待后再调用检查回调，不能只在弹窗前检查。
7. 读回目标后保存成功。若中途失败，保留 N3 暂停及原结果，不自动退出考试软件回滚。

内核的 `IRemoteExamActions` 只提供录制保留、只读检查、准备 ExamAware2 和正常退出 ClassIsland。它没有自启动修改接口。**本轮适配器是测试夹具；这不等于已证明真实软件启动全程零自启动写入。**

### 独立暂停与重启保护

新增本地文件 `remote-exam-runtime.json`，与原 `classroom-mode.json` 分开。保存采用临时文件、Flush(true) 和替换；加载时检查版本、状态、容量、重复 ID 和暂停归属。发现未完成写入或损坏文件时保留原证据，并阻止自动录课。

Host 在启动调度器前加载该暂停。调度条件取旧课堂模式暂停与 N3 暂停的并集；N3 只阻止新自动录制，不调用会停止自动录制的旧模式暂停函数，不改变用户启用状态、录课规则或历史。

重启时，进行中的 N3 记录变为 UNKNOWN，并保持暂停；不重新启动或退出软件。相同操作 ID 的相同请求返回原记录；同 ID 不同内容拒绝。历史暂设 256 条硬上限，达到上限拒绝新请求，不默默删除去重记录。正式对接前仍需冻结历史归档策略。

### 录制空闲保留权

`RecordingService.ReserveIdleForRuntimeAsync` 在与录制启动相同的同步边界中检查空闲并建立保留权。保留期间拒绝新的手动录制，调度器不启动自动录制；状态查询仍可用，不长期持有录制服务的信号量。

Starting、Recording、Pausing、Paused、Saving、未知状态，以及工作进程尚未退出的状态均不可保留。释放支持幂等调用，旧保留权不会重复释放新保留权。

另有 `RuntimeOperationGate` 提供“进行中普通变更 / 独占切换”的协调原语。**所有现有软件管理、旧课堂模式、前台直接管理员操作和通知弹窗尚未全部接到这个协调器上，因此生产远程入口仍未开放。**

### 现场结束的内核方法

`EndLocallyAsync` 只供后续本地 UI 适配器使用。它要求匹配当前修订与暂停来源，并再次保留空闲录制器、读取现场状态；若仍有外部动作待确认则拒绝解除。它只解除本次 N3 暂停，不启动/退出软件、不修改旧课堂模式。

成功任务的 `resolvedAt` 用于释放任务槽位，独立暂停继续存在。`pauseOperationId` 记录当前暂停来源，`locallyEndedAt` 另记现场结束；PARTIAL/UNKNOWN 的现场处理保留原 outcome。新请求被拒绝不会解除之前成功任务的暂停。

目前没有新增可点击的现场结束按钮，也没有提供可被网页调用的“返回日常”接口。

## 契约协作结果

NPClassworks / KV 协作任务新增了 [N3.0 服务端审阅](https://github.com/tempChanghong/NPClassworksKV/blob/main/docs/NPEP-N3-SERVER-CONTRACT-REVIEW.md) 和文档目录内的 JSON Schema / 正反例。双方确认需要：

- 持久递增的 policyRevision，防止旧开关重传覆盖新关闭。
- 独立 controlEpoch；暂停、恢复、Host 启动更换周期，旧任务不随恢复自动执行。
- N3 单独的状态序号，不改变 N1 0.1 / N2 0.2 含义。
- 当前补报会话与原执行会话分开，重启可补报但不能续跑旧许可。
- 已批准开始后失联保留 UNKNOWN，不自动释放槽位或再发一次。
- 创建/开始时重新验证当前发起人的账号、会话、角色及设备绑定。
- 原结果与现场解决记录分开；成功释放任务槽位不等于解除录课暂停。

路由、逐字段上限、现场解决后的服务器记录、权限执行方式等仍是草案，**未冻结为已上线协议**。本地内核类型不是 HTTP DTO，后续须显式映射，不能直接序列化本地路径上报。

## 管理员权限处理

用户提出的“主程序已提升，子进程复用管理员权限”作为接下来权限接入的方向。当前代码仍使用 asInvoker；前台发现已有 Host 时直接复用，因此只提升前台不能证明 Host 已提升。本轮没有贸然改变共享 manifest。

下一批必须同时处理实际 Host 权限检测、已存在普通权限 Host 的安全交接、首次授权与拒绝后的提示，以及 UAC 返回后再验证许可。不能结束正在录制的 Host 来完成提权。若选择应用整体管理员运行，应给主程序独立 manifest，并验证开机启动方式与 N1/N2 本地通信兼容，而非连带修改触控辅助程序的共享 manifest。

## 验证与证据边界

执行命令：

```powershell
./scripts/dotnet.ps1 restore tests/NPEduTools.Tests/NPEduTools.Tests.csproj --locked-mode --verbosity quiet
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --filter FullyQualifiedName~RemoteExamTests --no-restore
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-build --no-restore --logger "trx;LogFileName=n3-core.trx" --results-directory .artifacts/n3-kernel-tests
```

新增 N3 测试覆盖执行顺序、已满足目标、前置拒绝、UAC 后模拟撤销/配置变化、部分执行、并发保留、重放、重启、写盘失败、损坏记录、现场结束、独立暂停并集和容量保护。`RecordingService` 的空闲保留与手动启动拒绝使用实际服务类；软件进程/UAC 动作用夹具模拟，不会碰生产配置。

最终运行数量与构建结果见本报告末尾的验收记录。真实 Windows UAC、软件进程身份、自动录制与切换竞争、实际遮罩显示顺序、生产权限和网页按钮尚未验收。

## 下一批工作

1. 完成 N3.1：真实只读预检/软件适配器、所有本地变更入口的协调、N2 通知展示保留权、实际管理员执行上下文。
2. 增加大屏首次许可开关及现场核实/结束入口，持久绑定配对身份和控制周期。
3. 冻结 0.3 契约后实现服务端队列、短期开始许可、事件补报及网页进度，在隔离环境联调。
4. 单台设备真机验收通过后再更新试用 ZIP、推送和部署。

## 验收记录

- 新增 N3 测试：45/45 通过；结果文件 `.artifacts/n3-kernel-tests/n3-kernel.trx`。
- 完整核心回归：397/397 通过，无跳过，其中包括新增 45 项 N3 测试；结果文件 `.artifacts/n3-kernel-tests/n3-core.trx`。
- WPF 主程序及 Host Release 构建通过，0 警告、0 错误。命令：`./scripts/dotnet.ps1 build src/NPEduTools.App/NPEduTools.App.csproj --configuration Release --no-restore --verbosity quiet`。
- 服务端文档 Schema：在 KV 目录实际复核 29/29 结构与 UTF-16 正反例通过，36 个定义可编译。列出的 10 个数据库/授权语义场景尚未执行，不能计入端到端验收。
- 迁移目录后的依赖路径已通过 locked restore 重新解析；未修改依赖锁文件。KV 的文档校验使用临时目录内 Ajv，不改生产依赖，具体复现方式见服务端审阅文档。
