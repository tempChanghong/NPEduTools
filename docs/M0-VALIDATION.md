# M0 兼容性与验证记录

日期：2026-09-06。范围：ClassIsland 只读状态查询原型。

本文保留 M0 完成时的实现与 21 项测试记录。后续新增的 WPF、常驻订阅和自动重连已单独记录在 [M1 只读桌面验收记录](M1-READONLY-VALIDATION.md)；下方“未实现”项描述 M0 当时状态。

## 1. 环境与版本

| 项目 | 本次取值 |
| --- | --- |
| 系统 | Windows x64，build 26200 |
| 初始 .NET 环境 | 已有 .NET 8 / 10 运行时，无 SDK |
| 后续系统 SDK 更新 | 已核实 8.0.424 与 10.0.400，开发脚本改为优先使用兼容的系统 SDK |
| ClassIsland 构建 SDK | 用户补充安装 9.0.317，已完成本地 Debug 构建与真实 IPC 联调 |
| 项目内 SDK | 10.0.400，官方 ZIP 的 SHA-512 校验通过 |
| 项目内运行时 | Microsoft.NETCore.App 10.0.11 |
| 编译目标 | net10.0 |
| ClassIsland GitHub latest 发布 API 返回 | 2.1.0.1，2026-06-27 发布 |
| 固定 SDK 包 | ClassIsland.Shared.IPC 2.1.0.1 |
| 上游包标注源码提交 | 15273f82c9d2d55929df83b5fb806e68ee4547c0 |
| 固定通信库 | dotnetCampus.Ipc 2.0.0-alpha410 |
| 通信库包标注提交 | 99288944a929dc4ebe53a2d5418f2af2265459d9 |

选择 .NET 10 作为原型基线，依据为微软的 [支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)：.NET 10 为 LTS。本次不承诺旧版 Windows 兼容，也不把开发机配置等同于教室设备最低要求。

NuGet 中可见高于 2.1.0.1 的包版本；本次采用 GitHub latest 发布对应的包版本，不将包版本大小视作已完成的稳定版本兼容验证。

后续已核对旁边的 `D:\WebstormProjects\ClassIsland`，其 HEAD 与上述 NuGet 包提交相同、标签为 2.1.0.1。本地文档与开发环境差异详见 [本地联调环境](LOCAL-DEVELOPMENT.md)。

## 2. 已核实的接口

- [IpcClient](https://github.com/ClassIsland/ClassIsland/blob/2.1.0.1/ClassIsland.Shared.IPC/IpcClient.cs) 使用 `ClassIsland.IPC.v2.Server`。`Connect()` 不接受取消信号或超时时间。
- [IPublicLessonsService](https://github.com/ClassIsland/ClassIsland/blob/2.1.0.1/ClassIsland.Shared.IPC/Abstractions/Services/IPublicLessonsService.cs) 提供同步属性读取，接口特性声明 `IgnoresIpcException = true`。
- [事件 ID](https://github.com/ClassIsland/ClassIsland/blob/2.1.0.1/ClassIsland.Shared.IPC/IpcRoutedNotifyIds.cs) 包含上课、课间、放学与时间状态变化通知。原型在连接前注册处理器。
- [URI 服务](https://github.com/ClassIsland/ClassIsland/blob/2.1.0.1/ClassIsland.Shared.IPC/Abstractions/Services/IPublicUriNavigationService.cs) 的导航方法返回 `void`，不能据此证明控制动作完成；本阶段未开放调用。

读取多个课程属性不是原子快照。原型返回采样开始和结束时间，未将这些字段用于有副作用的场景决策。以后需要明确一致性检查或服务端快照接口。

## 3. 实现中验证出的约束

### 3.1 默认异常忽略不能靠普通配置覆盖

固定版本的 [IpcProxyConfigs](https://github.com/dotnet-campus/dotnetCampus.Ipc/blob/99288944a929dc4ebe53a2d5418f2af2265459d9/src/dotnetCampus.Ipc/CompilerServices/GeneratedProxies/IpcProxyConfigs.cs) 优先级低于接口特性。直接设置 `IgnoresIpcException = false` 不足以覆盖 ClassIsland 的默认行为。

原型使用 `StrictLessonsShape`，对实际读取的属性显式设置错误传播和 2 秒单次调用超时。整体请求还受 Host 的独立截止时间控制。

### 3.2 alpha410 的形状代理注册不一致

该版本生成器输出 `AssemblyIpcProxy`，但 [运行时工厂](https://github.com/dotnet-campus/dotnetCampus.Ipc/blob/99288944a929dc4ebe53a2d5418f2af2265459d9/src/dotnetCampus.Ipc/CompilerServices/GeneratedProxies/GeneratedIpcFactory.cs) 只扫描 `AssemblyIpcProxyJoint`。实际运行因此抛出“未生成代理壳”异常。

`ProxyRegistration.cs` 添加一条兼容注册，将现有生成代理关联到形状类型。它不更改外部协议、不读取或修改库的私有字段。升级通信库时必须复核是否仍需该注册。

该生成器对非空返回值存在可空分析告警：仅适配器项目的生成文件使用 annotations 模式，所有手写适配器代码显式 `#nullable enable`，不全局关闭可空检查或警告即错误规则。

### 3.3 同步读取必须移出连接完成的执行上下文

测试中，连接后立即调用同步 getter 会超时；增加观察等待后查询可以成功。将所有同步属性读取放入独立 `Task.Run` 后，无等待查询及故障测试均通过。结合生成 getter 内部的 `Task.Result`，判断与通信接收执行上下文被阻塞有关；后续升级需继续保留此回归测试。

### 3.4 超时由进程边界兜底

Host 每次查询创建一个独立探测进程，并限制同一时刻只有一个 ClassIsland 查询。截止时间包含进程启动、连接、事件观察和状态读取；超时或取消后，Host 终止自己创建的探测进程并等待清理。

此阶段所有外部操作只读，因此可以终止探测进程。未来有副作用的能力不能直接套用这种重试或终止语义。

Host 意外退出时，探测进程的 20 秒自退出计时器限制孤立进程寿命。尚未实现 Windows Job Object 和生产级守护恢复。

### 3.5 权限与连接边界

Host 自有管道使用 CurrentUserOnly 并验证客户端进程会话。ClassIsland 的管道及其权限由上游管理，原型不会修改。

开发沙箱中，第三方管道连接曾返回 AccessDenied；在正常用户权限下运行测试后，才能验证实际协议行为。自动化测试需要能够创建及连接本机命名管道，不能把沙箱拒绝访问解释为 ClassIsland 协议不兼容。

固定上游管道名不含用户会话信息。原型尚未验证真实 ClassIsland 对跨用户、跨会话及恶意同用户管道抢占的防护；只读探测不得直接扩展为可信控制入口。

## 4. 验证方式

运行 [验证脚本](../scripts/verify.ps1)，它按锁文件还原、Release 构建并执行测试，TRX 输出到 `.artifacts/test-results/prototype.trx`。

测试覆盖以下行为：

- 消息长度上限、截断帧、未知字段、协议版本、能力白名单与超时参数。
- 使用真实 `dotnetCampus.Ipc` 管道和 ClassIsland 官方契约查询模拟科目、枚举及课表状态。
- 接收上课事件，以及将无课表成功结果与连接失败区分。
- 服务未启动、连接后无响应、查询中退出与服务端抛异常。
- 服务出现及重启后重新查询，客户端断开与重连，CLI 跨进程查询。
- 取消、资源冲突、超时后资源释放，以及异常客户端不影响后续连接。

本轮结果：Release 构建 0 警告、0 错误；21 项测试全部通过，无跳过项，测试耗时约 11 秒。结果文件为 `.artifacts/test-results/prototype.trx`。

测试服务端是自行实现的协议测试替身，不是完整 ClassIsland 应用。

随后完成真实 `ClassIsland 2.1.0.1` Debug 本体验证：自然课程变化、课程事件、退出超时与重启恢复均通过，详见 [真实联调记录](CLASSISLAND-LIVE-VALIDATION.md)。真实结果与上述模拟服务端测试分别记录。

## 5. 架构落实程度

| 项目 | 状态 |
| --- | --- |
| Core / Contracts / Host / Adapter 分离 | 已落实到最小工程 |
| WPF App | 暂由 CLI 验证进程边界，未实现界面 |
| 状态查询与有限事件观察 | 已实现，事件不跨连接持久化 |
| 场景引擎、软件启动/关闭、恢复日志 | 未实现 |
| 请求去重、结果历史、状态订阅流 | 未实现，目前只有只读请求 |
| 结构化诊断 | 标准错误输出，尚无文件轮转与导出 |
| 自动重连 | 每次显式查询创建新连接；未实现常驻订阅重连 |
| 进程隔离 | 因实际阻塞风险提前实现只读探测进程 |
| 真实 ClassIsland 版本兼容性 | 本地 2.1.0.1 Debug 本体只读 IPC 验证通过，正式发行包与教室环境尚待验收 |
| PowerPoint / WPS / 其他软件 | 未验证、未接入 |

## 6. 后续验收

1. 使用正式发行包、教室实际档案和目标设备重复只读联调。
2. 验证 UI 显示、权限差异、多用户会话、睡眠唤醒与长期运行。
3. 设计持续事件订阅与断线后的状态重同步；当前短时观察存在连接间隙。
4. 确认首个写操作的正式接口与结果验证方式，再实现 WPF 最小界面和场景执行。
