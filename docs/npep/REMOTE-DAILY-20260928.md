# 远程结束考试：返回日常

2026-09-28 · 本地实现，未推送或部署。沿用学校配对授权，无新增许可开关。

```mermaid
flowchart LR
  A[网页：结束考试／返回日常] --> B[暂让通知／保存录制]
  B --> C[ClassIsland 自启动开／ExamAware 自启动关]
  C --> D[正常退出 ExamAware]
  D --> E[启动 ClassIsland 并核实课程接口]
  E --> F[保存日常模式／解除远程录课暂停]
  D -->|放映或退出受阻| G[明确原因／保留暂停／允许重试]
  E -->|启动未就绪| G
```

## 使用

重启本地前后端与 IDE 中的 App／Host。在网页 **学校管理 → 大屏 → NPEP 设备互联 → 远程考试模式**，核对设备并勾选确认，点击 **结束考试／返回日常**。

成功回执必须同时包含：ExamAware 已退出、ClassIsland 已就绪、日常自启动已应用、远程录课暂停已解除。恢复的是原录课计划判断，不会把原本关闭的录课总开关打开。

- 正在放映时先在大屏结束放映。编辑器或退出确认阻止正常退出时，不强杀进程；处理后再发起返回请求。
- 未确认 ClassIsland 就绪或任一步骤失败时，保持录课暂停，网页报告具体原因。
- 同方向活动请求合并；相反方向请求等待当前任务结束后再提交。请求丢失响应时沿用相同编号，重启不自动重放。
- 再次返回已就绪的日常环境，不重复启动或退出软件。历史考试回执仍记录当时事实，不被改成日常回执。
- 桌面原有“结束远程考试状态”仍仅用于本地解除暂停，与本次网页完整返回操作区分。

## 代码入口

| 层 | 关键入口 |
|---|---|
| 网页 | `NpepRuntimeControl.vue`、`useNpepRuntimeControl.js` |
| 服务端 | `npepRuntimeService.js`、`runtimeControl.js` |
| 桌面执行 | `RemoteExamExecutor.cs`、`RemoteExamActions.cs` |
| 日常模式 | `ClassroomModeService.RemoteExam.cs`、`ClassroomModeEffects.RemoteDaily.cs` |
| 正常退出／启动 | `ExamAwareService.QuitUnderRuntimeLeaseAsync`、ClassIsland 固定 `runtime-launch-mode` 操作 |

N3 0.4 增加固定目标 `DAILY`，通过 `remoteDailyControl` 能力标识区分旧桌面；没有新增数据库迁移或桥接命令。需要同步更新后端、网页和桌面。ExamAware 退出后，针对当前适配的 1.5.2 读取 Windows 当前用户 Run 中 `ExamAware` 登记是否已移除；仅只读核查，自启动写入仍通过桥接。

## 验收

统一入口：`scripts/test-npep-exam-plans.ps1 -Browser -Database`。使用隔离浏览器和原生临时 PostgreSQL；实际 .NET 联网及桥接协议参与测试，操作系统软件效果使用替身。

本轮通过：网页 13 项、后端 29 项、Host／桥接 143 项、联网层 127 项、管理员组件隔离验证 11 项；N3 跨端契约 31 个样例；浏览器包含返回日常及丢失响应重试；原生 PostgreSQL 21 项全部通过，含真实 .NET 的 DAILY 回执。WPF 隔离编译为 0 警告／0 错误。测试未修改真实软件运行状态或 Windows 自启动。

记录：桌面 `.artifacts/remote-daily-acceptance.log` 保留最初浏览器按钮标签失败，修正后的浏览器通过记录在网页 `.artifacts/remote-daily-browser.log`；数据库最终结果在桌面 `.artifacts/remote-daily-database.log`，管理员组件在 `.artifacts/remote-daily-admin.log`。

2026-09-28 人工验收更新：用户已回报远程返回验证通过，并确认完整考试流程走通。记录为用户回报的单设备人工验收，不补写未提供的机器、请求编号或生产结论。重复返回、退出受阻与 ClassIsland 未就绪等边界不因主流程通过而自动标记为现场通过。当前汇总见[考试互联用例与验收](EXAM-MODE-OVERVIEW.md)。
