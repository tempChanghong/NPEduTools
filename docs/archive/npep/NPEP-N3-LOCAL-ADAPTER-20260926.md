# N3.1 本机软件适配与协调接入

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

日期：2026-09-26。状态：代码已接入，本轮未构建、未执行测试、未做真实软件切换。用户在 Visual Studio 内运行 Debug；不再例行提供构建或 ZIP。

承接 [执行内核](NPEP-N3-KERNEL-IMPLEMENTATION-20260925.md)。生产 Host 已创建真实软件适配器和执行器，**只开放本地只读检查，不开放执行、解除暂停或远程控制接口**。首次学校控制许可与 HTTP 下发尚未实现。

## 在 IDE 里可以看到什么

在“设置 → 学校互联”找到“远程考试环境 · 本机检查”，点击“检查考试环境”。

按钮检查录制器能否保留为空闲、实际 Host 权限、本地配置与文件、运行实例身份、桥接及旧模式待恢复状态。不启动/退出软件，不修改计划任务或登录自启动，不写新的 N3 执行记录。

| 检查结果 | 含义 |
| --- | --- |
| HOST_NOT_ELEVATED | 实际 Host 为普通权限；不以 VS 或前台窗口的权限代替判断 |
| RECORDING_BUSY | 录制器工作中或状态未确认，不停止录制 |
| OPERATION_BUSY | 通知窗口、软件管理或另一项检查仍占用运行环境 |
| DESKTOP_UNAVAILABLE | 交互桌面不可用 |
| 配置/实例错误 | 路径未保存、文件不受支持、其他路径/用户/会话的进程，或实例不能核实 |
| 检查通过但桥接未就绪 | 本机条件通过；实际切换仍须准备并确认 ExamAware2 |

F5 可能复用此前已运行的 Host。查看新代码前，在没有录制或待完成操作时正常退出整个 NPEduTools，再运行 Debug。仅提升前台不会接管已存在的普通权限 Host。

后续诊断修正：程序校验按 ClassIsland、ExamAware2 的顺序进行。此前统一返回 INVALID_LOCAL_EXECUTABLE，无法判断哪一个路径有问题；现分别返回 CLASSISLAND_EXECUTABLE_INVALID、EXAMAWARE_EXECUTABLE_INVALID 或 EXAMAWARE_EXECUTABLE_UNREADABLE，并在本地保留验证器的具体原因。输入框内显示新路径不代表配置已保存，桥接连接也不代表两个已保存路径都有效。详细原因仅供本地提示，执行记录仍只保存错误码。

## 实现导航

```text
MainWindow.RemoteExam.cs
  └─ 本地 IPC remoteexam.preflight
       └─ RemoteExamExecutor.PreflightAsync
            ├─ RuntimeOperationGate：独占检查/切换位置
            ├─ RecordingService：原子检查并暂时保留空闲
            └─ RemoteExamActions.InspectAsync
                 ├─ LaunchService：本地 ClassIsland 配置
                 ├─ ExamAwareService：桥接与指定进程
                 ├─ ClassroomModeService：旧模式待恢复状态
                 └─ WindowsRemoteExamPlatform：文件、权限、会话、进程
```

未来通过可信授权适配器进入 RunAsync 后，同一套 RemoteExamActions 先准备 ExamAware2，再正常退出 ClassIsland。没有临时免授权执行入口。

### ExamAware2

只使用本地保存的位置和配对修订，核实路径、用户和会话。Connected 后还要捕获桥接所指进程并验证身份。已存在未连接实例时等待桥接，不再次启动；不存在实例时调用固定内部启动方法，不接受网页 EXE、URI 或参数。

准备阶段最多等待 18 秒，持续重新检查授权、配置和就绪状态；超时或撤销不退出 ClassIsland。执行意图由内核在启动前持久化，失败不自动重放。不要求 CanSetAutoStart 权限或自启动登记结果。

### ClassIsland

旧 close 动作要求自启动任务存在，不能复用。本轮新增 ClassIslandRuntime 和 runtime-close，由管理员组件独立分支处理，**不创建 ScheduledStartup、不访问 Task Scheduler**。

核对完整路径、SID、会话、PID 和进程启动时间后，仅请求已核实实例正常退出。实例变化就停止，不补发、不强制结束 ClassIsland。窗口拒绝、超时或结果不明时，内核保留部分完成状态。

AdminClient 在组件连接后、向管道发送操作前再次执行检查回调。已有管理员权限时子进程直接继承权限；普通权限的旧本地管理员按钮仍按需使用 Windows UAC。

## 跨进程协调

RuntimeOperationGate 除内存计数外，生产实例使用 RuntimeOperationFile。协调文件位于当前用户 LocalAppData 下的 NPEduTools/coordination/runtime-session-{会话号}.lock。文件内容没有业务含义，不靠文件是否存在判断忙闲。

普通操作持共享文件句柄，N3 检查/切换持独占句柄。句柄可跨异步线程保留，进程退出后由 Windows 释放，不使用超时强行接管。前台、Host 和管理员组件共用同一文件。

| 入口 | 保留到何时 |
| --- | --- |
| LaunchService 配置、启动、验证 | 后台验证结束，而非 IPC 返回 Running 时 |
| ExamAwareService 配置、启动、退出、自启动、撤销配对 | 对应后台操作完成 |
| 旧 ClassroomModeService | 整个模式操作完成，包括恢复/重试 |
| 录制控制与自动录制设置 | 请求处理结束；会话空闲另由录制器原子保留检查 |
| 前台管理员按钮 | AdminClient 返回；派发后的组件另持自身句柄，覆盖前台退出 |
| 学校通知和通知预览 | 窗口关闭；获取内容、创建窗口前已保留 |
| Host 停止 | 停止请求处理结束；独占期间拒绝受理 |

只读状态与录制心跳仍可用。切换中通知继续轮询、接收、保存，仅延后展示，包括紧急通知。旧版程序不懂这把协调锁，需要一起更新程序集。

该机制覆盖 NPEduTools 自身入口，不能禁止用户直接操作外部软件，因此动作边界仍核对配置、进程与桥接。

## 权限边界

**未修改 manifest，未自动重启/提权 Host，也不会为了提权结束录制。** 普通权限 Host 在建立 N3 录课暂停之前被拒绝。

应用整体管理员启动策略、已有普通权限 Host 的安全交接和首次权限引导仍需后续实现。不能以“前台已提升”替代真实后台权限验收。

## 验证状态与 IDE 验收

本轮进行源代码审阅、Git 差异空白检查和 XAML XML 结构检查。未构建、未执行新测试、未切换真实软件、未推送部署。此前 397 项核心测试的结果不能证明本轮代码通过。

新增 9 个测试方法，等待在 IDE 测试资源管理器执行：

- RemoteExamTests：预检不写历史、普通权限在暂停前拒绝、协议仅开放预检。
- ExamAwareTests.RemoteExam：实际 Host 服务配合模拟 OS 边界，验证不依赖自启动权限、派发前撤销、未就绪不关闭源软件、普通权限拒绝、服务互斥和只读访问。
- LaunchTests：返回 Running 后仍保留互斥，直到后台验证完成。

测试中的进程和授权行为使用夹具，不等同于真实 UAC 验收。建议先在测试资源管理器执行三个测试类，再验证检查按钮。跨进程互斥、通知遮罩、进程中途退出释放，以及真实软件正常退出仍需 Windows 实测。

## 下一步

1. 完成管理员启动策略与已有 Host 的安全交接，补充分进程互斥验收。
2. 增加与配对身份绑定的首次许可、控制周期、本地核实/结束入口。
3. 冻结 0.3 契约，接入服务器命令、开始许可、结果回传与网页按钮。
4. 单台设备真实验收；发布时再单独安排打包。
