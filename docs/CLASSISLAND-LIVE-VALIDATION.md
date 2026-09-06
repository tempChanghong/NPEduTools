# 真实 ClassIsland 联调记录

完成时间：2026-09-06 12:00:31（Asia/Shanghai）。

结论：NPEduTools 对本地 ClassIsland 2.1.0.1 Debug 本体的只读 IPC 联调通过。课程状态与事件由真实课表引擎产生，未使用模拟 IPC 服务端或远程属性写入。

## 环境与构建

| 项目 | 记录 |
| --- | --- |
| Windows | x64，build 26200 |
| ClassIsland 源码 | `D:\WebstormProjects\ClassIsland` |
| 源码提交 | `15273f82c9d2d55929df83b5fb806e68ee4547c0`，标签 `2.1.0.1` |
| ClassIsland SDK | 系统 .NET SDK 9.0.317 |
| ClassIsland 运行目标 | `net8.0-windows10.0.19041.0` |
| 本体程序集产品版本 | `2.1.0.1+15273f82c9d2d55929df83b5fb806e68ee4547c0` |
| NPEduTools | 系统 SDK 10.0.400，Release，net10.0 |
| 本体 ClassIsland.dll SHA-256 | `FEAAA48CB951C91938720BE9C8169E1FBD6CD0B41B230D9D8F3BE54DA26F4231` |

本体首次构建遇到引用程序集缺失，时间上与用户的清理/构建操作重叠。之后串行重试成功，0 错误、1 条 `System.Collections.Immutable` 程序集版本冲突警告；未为了通过构建更改 ClassIsland 源码，也未将并发时的失败判定为已证实的上游代码缺陷。

本轮使用的核心构建命令（从 ClassIsland 目录运行）：

```powershell
dotnet build ClassIsland.Desktop/ClassIsland.Desktop.csproj -c Debug --no-restore -m:1 -p:Version=2.1.0.1 -p:NuGetVersion=2.1.0.1
```

此命令要求已完成依赖还原。本轮还原使用 NuGet.org，依赖缓存位于 NPEduTools 的 `.tools/nuget`。

## 验证过程与结果

脚本复制实际 Debug 构建产物到临时目录，并通过 ClassIsland 支持的 `ClassIsland_PackageRoot` 与 `PackageType=folder` 指定测试数据目录。该目录包含专用档案、科目和时间表。

| 步骤 | 实际结果 |
| --- | --- |
| 启动真实本体并加载测试档案 | `Succeeded`，`OnClass`，科目“ NPEduTools 实机联调 A ” |
| 时间到达第一节课末尾 | `Breaking`，科目/显示名称“联调课间” |
| 观察真实课间事件 | `classisland.lessonsService.onBreakingTime` 共收到 1 次 |
| 时间到达第二节课开始 | `OnClass`，科目“ NPEduTools 实机联调 B ” |
| 观察真实上课事件 | `classisland.lessonsService.onClass` 共收到 1 次 |
| 终止本次创建的临时本体 | Host 查询返回 `TimedOut` / `ClassIslandDeadlineExceeded`，状态数据为空 |
| 再启动同一个临时本体 | 同一 Host 成功返回课程 B，无需重启 Host |
| 测试结束 | 本次创建的本体、副本重启实例和 Host 均关闭 |

关闭目标后的查询设置 600 ms 超时，Host 记录耗时为 634 ms（包含清理开销）。这是一条本机测量记录，不是性能保证。

首次查询后，脚本以 5 秒观察窗口连续查询，实际看到了上课和课间两种状态，以及随后恢复到课程 B。事件接收仅证明本轮观察窗口内的链路可用，尚不能保证跨连接事件无遗漏。

## 本地证据

本轮运行 ID：`f73a8abf3d414c04b66e849c267fb339`。文件保留在忽略提交的 `.artifacts` 目录：

- [汇总结果](../.artifacts/classisland-live/f73a8abf3d414c04b66e849c267fb339/summary.json)
- [11 次查询响应](../.artifacts/classisland-live/f73a8abf3d414c04b66e849c267fb339/responses.json)
- [Host 结构化日志](../.artifacts/classisland-live/f73a8abf3d414c04b66e849c267fb339/host.stderr.log)
- [ClassIsland 日志](../.artifacts/classisland-live/f73a8abf3d414c04b66e849c267fb339/classisland.stdout.log)
- [串行构建日志](../.artifacts/classisland-build-serial.log)

其他克隆环境没有这些临时文件时，可通过下述脚本重新生成自己的证据目录。

## 复现

先完成 NPEduTools Release 构建与 ClassIsland Debug 构建，然后从 NPEduTools 目录运行：

```powershell
./scripts/test-classisland-live.ps1
```

本体输出路径不同的环境可以显式指定：

```powershell
./scripts/test-classisland-live.ps1 -ClassIslandBinary 'D:/WebstormProjects/ClassIsland/ClassIsland.Desktop/bin/Debug/net8.0-windows10.0.19041.0/ClassIsland.Desktop.exe'
```

脚本要求没有正在运行的 ClassIsland 实例；它会检查进程及上游全局互斥锁。测试仅终止自己创建的进程，不按软件名称批量结束进程。请在午夜前至少两分钟运行，避免测试时间表跨日。

档案和设置位于临时包目录；上游机器级 GlobalStorage、全局互斥锁及 IPC 管道仍使用原有实现，不属于完整的用户配置或安全沙箱隔离。测试期间禁用测试档案的语音与提醒，并隐藏其主界面；本轮验证的是 IPC 行为，未验证视觉呈现。

## 后续范围

- 正式发行包、真实教室设备与长期运行验证。
- 常驻事件订阅、断线后的完整状态同步。
- 软件启动或其他写操作的正式接口及验证方式。
- WPF 最小界面与后续场景执行。

本次成功不代表上述功能已经实现，也不扩大对其他 ClassIsland 版本的兼容承诺。
