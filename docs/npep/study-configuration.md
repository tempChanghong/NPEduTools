# 自习配置与执行接入（协议 0.10）

配置和实际采集分开。GET `/api/v2/npep/device/study-settings` 只读配置；POST `.../receipt` 记录配置应用；POST `.../execution` 在当前身份、会话、版本及新鲜应用回执下确认独立运行租约并记录执行事实。配置响应的 `executionState=NOT_VERIFIED` 不从历史回执推断采集。

`StudySettingsService` 计算准备、进行、关闭和阻塞阶段，把新规则及已确认许可交给已有 `NoiseScheduleService`。这个唯一执行器复用原来的排程所有权、停止记录、麦克风故障保护和统计队列，不另建采集器。新规则执行生效日期和学校日历；准备阶段不采集，时段为左闭右开。手动会话不被接管或停止。

新策略初次读取前阻挡旧排程抢先采集。明确未配置时允许旧规则；服务器明确不支持时允许尚未接管的旧协议运行。配置过的新策略不会因错误或离线退回旧规则。KV 对不支持新日历的旧桌面客户端返回旧排程关闭规则；未配置班级保持旧指纹及规则。

学校时间沿用 offset-zero 日历载体及 ClassIsland 桥接，不转换为 UTC，也不使用 Windows 时间兜底。日历例外和生效日期作用于时段开始日，跨午夜延伸到次日，相邻时段合并。

十五秒是学校配置确认的新鲜窗口，执行许可由独立 exchange 确认，不持久化。普通网络故障撤下新鲜学校回执声明，但已见证执行的本次时段保持本机展示和统计，到点结束后撤销剩余离线许可；详见[失联续行](study-offline-continuation.md)。已有采集仍受本机日历、学校时间、模式及存储保护；暂停、解绑和明确授权/版本/协议拒绝撤销许可。Host 重启不恢复磁盘租约。离线无法获知服务器新修改，重连后核对。

`study.status` 的 `Execution` 与 `CaptureState` 是本机执行事实。0.10 配置回执中的 `captureState=NOT_INTEGRATED` 保留给旧解析器，不能用于判断当前采集；实际执行通过独立 exchange 提交。只有定时所有者处于 Active 并收到新鲜有效采样，才设置 `signalFresh=true`。数字静默是有效采样，有限电平仍为空；削波不换算为声压级。

学校后台分开显示应用、启动中、设备回报正在采集及历史回执，按当前版本、会话和新鲜度撤下过期标识。正式 App 已通过本地 `study.status` 接入 WPF 自习窗口；`study.hide` 只控制 Host 单调收起期限，`study.reminder` 只批准当前阶段一次提示音，均不操作采集。当天作业、展示回执、双阈值提示与按钮查看自习座位表已接入，分别见 [study-homework.md](study-homework.md)、[study-presentation.md](study-presentation.md)、[study-thresholds.md](study-thresholds.md) 和 [seating.schema.json](seating.schema.json)。OOBE 与自习管理页可保存监测麦克风、预留捕获摄像头；照片捕获与上传、声压级校准仍未实施。旧 0.7 恢复入口仍拒绝新策略的版本；新策略恢复使用独立 0.11 通道，见 [study-resume.md](study-resume.md)。

验证仅使用合成采集、虚拟学校时间、隔离 Pipe、真正本机 HTTP 与内存仓储，未执行数据库迁移或使用真实硬件。完整审核见 `D:/CodeProjects/NPEP-study-20261009/execution-review/REVIEW.md`。

最新展示增量、截图及内存边界见 `D:/CodeProjects/NPEP-study-20261009/display-review/REVIEW.md`。`Display.Available` 不是实际已显示的回执；网络配置失败不会复活旧备用窗口。
