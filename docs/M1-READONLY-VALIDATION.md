# M1 只读桌面与持续监听验收

日期：2026-09-06。状态：本阶段只读窗口交付通过，M1 的外部软件启动、配置存储和执行记录尚未完成。

## 已交付

- `NPEduTools.App`：独立 WPF 进程，展示课程、课表与连接状态，按需启动随构建附带的 Host。
- 关闭窗口保留 Host；重新打开获取完整快照。Host 意外退出时窗口会重试启动并重新订阅。
- Host 共享常驻 ClassIsland 监听进程；UI 离线时继续接收事件，多个订阅不重复创建上游连接。
- 上游连接失效或响应超时后发布无课程数据的失败状态，并退避重连；成功连接后重新读取全部字段。
- “停止后台并退出”先取消本窗口的重连，向 Host 请求停止并等待实例退出。Host 向其他已连接窗口发送停止通知，使其不会把主动退出当作崩溃恢复。
- CLI 保留 `ping` / `status`，增加 `watch` / `stop`。

App 只依赖 Contracts；Host 作为不引用程序集的构建依赖复制到 `Host/` 子目录。App 不直接连接 ClassIsland，也不管理其进程或档案。

## 监听与协议决策

第一名订阅者按需启动监听，最后一名订阅者离开后监听继续运行，直到 Host 停止。原有单次查询仍使用自己的短生命周期工作进程，两类操作均为只读。

上游处理器在连接前注册。收到通知后合并短时突发，最短采样间隔为 200 ms；没有通知时约每 1.2 秒补读一次。同步 getter 保留 `Task.Run` 隔离，避免阻塞第三方接收循环。任何一次完整快照超过 8 秒未返回时，Host 清理本次监听进程并重连。

重试等待为 1、2、4、8、15 秒封顶，成功读取后重置。断线期间不会继续公开上一节课的成功状态。目标故障检测有延迟，最坏受 8 秒工作进程期限和约 1 秒下发周期影响；这不是实时控制保证。

Host 每秒下发完整 `WatchSnapshot`，兼作心跳。每个订阅使用自己的 RequestId；StreamId 区分 Host 监听实例，Sequence 单调增长，ConnectionId 每次上游重连变化。相同 Sequence 是心跳，跳号可直接替换为新快照。事件计数是当前上游连接期间的累计值，不能跨 ConnectionId 相加，也不代表可重放的事件日志。

每个 Host 最多接受两个持续订阅，保留其他连接槽位处理查询与退出；慢客户端写入超过 2 秒断开，不阻塞上游监听。客户端 5 秒收不到任何帧即视为 Host 连接失效。WPF 自动恢复；CLI 报错退出，由调用方决定何时重连。

Host 每 2 秒通过标准输入续约监听进程。Windows 标准输入的阻塞读取未可靠响应取消，因此使用独立读取线程更新单调时间戳，由独立计时器检查：8 秒没有续约后，在下一个约 1 秒检查周期退出；输入关闭时立即退出。Host 只终止自己创建的工作进程，清理失败时停止创建新监听进程。

## 验证结果

| 验证 | 结果 |
| --- | --- |
| 锁文件还原、Release 构建 | 通过，0 警告、0 错误 |
| 自动化回归 | 27 / 27 通过，无跳过 |
| 原有只读 IPC 回归 | 21 项保留通过 |
| 新增常驻监听测试 | 6 项：22 秒无客户端仍保留连接、累计事件、目标重启后重同步、共享连接及订阅限额、hang/drop/error 恢复、租约失效退出；部分行为合并在同一测试中 |
| 主动退出通知 | 两名订阅者均收到无旧课程的 Stopped 快照，Host 退出 |
| 实际 WPF 控件验收 | 自动启动、窗口重开沿用 Host、目标断线清空课程、空课表恢复、Host 被终止后恢复、停止后台退出均通过 |
| 真实 ClassIsland 2.1.0.1 Debug 持续监听 | 同一健康连接观察 A → 课间 → B；本体关闭返回 Unavailable，重启后同一订阅、新上游连接恢复 B |

真实 ClassIsland 验证运行 ID 为 `7fc2a3fa7e874a92ab498cd964bc1cee`，完成于 12:42:01，收到上课 1 次、课间 1 次。没有重新发送 watch 请求来触发恢复。

本地证据（`.artifacts` 不提交，其他环境通过脚本重建）：

- [27 项测试 TRX](../.artifacts/test-results/prototype.trx)
- [真实持续监听汇总](../.artifacts/classisland-live/7fc2a3fa7e874a92ab498cd964bc1cee/summary.json)
- [真实订阅帧](../.artifacts/classisland-live/7fc2a3fa7e874a92ab498cd964bc1cee/responses.json)
- [WPF 控件验收记录](../.artifacts/app-smoke/4894cddd791340db9cf71a2d81d94ed7/summary.json)
- [正常窗口截图](../.artifacts/app-smoke/4894cddd791340db9cf71a2d81d94ed7/connected.png)
- [最小窗口截图](../.artifacts/app-smoke/4894cddd791340db9cf71a2d81d94ed7/compact.png)

已检查本机缩放下的正常窗口与最小窗口截图；底部退出按钮固定可见，内容区域在空间不足时滚动。这不替代多显示器与不同 DPI 环境验收。

## 运行与复现

从仓库根目录使用 PowerShell 7：

```powershell
./scripts/verify.ps1
./scripts/start-app.ps1
```

ClassIsland 未运行时，窗口显示等待/重连状态；自行打开本体后会自动同步。该窗口还没有“启动 ClassIsland”能力。

```powershell
./scripts/test-app-smoke.ps1
./scripts/test-classisland-live.ps1 -Watch
```

窗口测试打开可见 WPF 窗口并使用 Windows UI Automation 读取、点击实际控件，在私有测试管道上模拟课程。真实本体测试复用 M0 临时课表，要求没有其他 ClassIsland 正在运行，隔离范围和约束见 [实机记录](CLASSISLAND-LIVE-VALIDATION.md)。两种测试分别验证窗口与真实适配器，不把模拟窗口数据称为真实课堂数据。

## 尚未覆盖

- 正式发行版、真实教室档案、睡眠唤醒、多用户会话、全天运行及性能预算。
- 历史事件补发或恰好一次交付；ClassIsland 多属性读取也不是原子快照，当前只用于展示。
- ClassIsland 启动能力、路径配置、配置迁移、执行记录、持久化请求去重、场景与恢复。
- 托盘、开机启动、安装包、独立发布目录与日志导出；目前只能使用完整构建输出目录。

下一步是在现有窗口上加入可验证的 ClassIsland 启动能力，并落实 Host 配置持久化与操作结果记录，再进入场景编排。
