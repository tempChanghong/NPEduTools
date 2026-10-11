# NPEP 运维验收：补齐 .NET 传输联调 · 2026-10-04

| 任务卡 | 内容 |
| --- | --- |
| 主交付 | 统一运维入口的数据库阶段实际执行五类 .NET／HTTP／PostgreSQL 联调 |
| 原问题 | 过去 `-Database` 使用 `--skip-desktop`；桌面单元及字段对照无法替代这五类跨进程传输 |
| 范围 | 改测试计划、预检查和执行证据校验；复用现有验收程序和原生数据库运行器 |
| 边界 | 不改业务接口、网页、后端或 NPEssentials；不操作真实软件、硬件、Windows 自启动或生产服务 |
| 验收 | 五类场景各实际通过一次、所有数据库测试零跳过、临时集群清理、源码稳定 |

## 如何运行

```powershell
./scripts/test-npep-operations.ps1 -Browser -Database
```

兼容 PowerShell 5.1。不增加新参数；不选 `-Database` 时仍明确标记数据库阶段 `NOT_REQUESTED`。`-CheckOnly` 只检查依赖和验收项目是否存在，不执行联调。

数据库阶段现在显式传入本次桌面源码根目录，不依赖后端目录旁碰巧存在的另一个 NPEduTools。原生运行器编译 `tests/NPEduTools.Npep.Acceptance`，输出在桌面 `.artifacts/n3-tests`；不运行 WPF 主程序，也不覆盖 IDE 的应用 Debug 输出。

| 必须实际执行的场景 | 检查内容 |
| --- | --- |
| 噪音监测 | 大屏凭据隔离、真实 .NET 收取启停命令、统计报告上报与采集释放；音频输入是合成替身 |
| N3 考试控制 | 考试／日常、部分完成、重复请求回执、暂停结束及重启不重复执行；系统操作是替身 |
| N4 考试方案 | 内容字节校验、准备与明确启动、重复投递和放映会话上报；播放器是替身 |
| 网页发起配对 | .NET 领取、确认、重启、上报、撤销以及 SQL 最终记录 |
| N4.3c 定时监测 | 0.6／0.7 调度与传输、管理／显示观测和会话报告；学校时钟及采集是合成替身 |

运行真实 C# 集成模块、Node HTTP 路由和 PostgreSQL；测试服务器限定回环地址，数据库是独立临时集群，不连接用户调试库或 Windows PostgreSQL 服务。成功后要求运行器明确确认集群已停止并清理。失败保留日志和失败状态，不退回跳过配置。

## 执行证据规则

`scripts/test-npep-operations.mjs` 中 `readDatabaseEvidence` 要求有效 TAP 计数且零跳过／取消／TODO／失败。五个指定场景必须各出现一次实际 `ok` 记录，同时要求运维概览 SQL 子用例和清理通过。缺少某项、同名重复、标成 SKIP／TODO，即使命令零退出或其余检查全部通过，也不能判为通过。

结果文件记录 `executedTransportScenarios`、`realHttp=true`、`realPostgreSql=true`、`simulatedSystemEffects=true`，继续保持 `realMicrophone=false`、`realSoftwareControl=false`、`releaseReady=false`。GitHub 工作流调用同一入口，提交／推送后会沿用这项要求；本地通过不是云端通过。

回归测试对五种场景逐一检查遗漏、跳过、TODO 和重复的拒绝行为，并保留概览遗漏与集群未清理的检查。

## 结果

Windows PowerShell **5.1.26100.9444**、Node **24.14.1**、.NET SDK **10.0.401**、原生 PostgreSQL 17 下，完整入口 `-Browser -Database`：**PASSED**，`sourceStable=true`，执行约 3 分 48 秒。

| 检查 | 结果 |
| --- | --- |
| 入口与证据回归 | 10 通过 |
| 桌面单元回归 | 59 通过 |
| 跨端状态导出／摘要对照 | 四组状态导出与本轮生成摘要对照均通过 |
| WPF 编译 | 通过，未运行应用 |
| 后端／网页单元与流程 | 20 + 5 + 7 通过 |
| 隔离浏览器 | 3 通过，无跳过或重试不稳定项 |
| 旧配对迁移 | 6 通过 |
| HTTP／PostgreSQL 业务联调 | 45 通过，0 失败、0 跳过；包含五类 .NET 传输场景 |
| 集群清理 | 运行器明确确认临时集群已移除 |

报告：`.artifacts/npep-operations/2026-10-04T11-13-44-649Z-ad1a2931/result.json`，数据库分项为同目录 `database.stdout.log`／`database.stderr.log`。本轮使用桌面未提交改动；网页和后端仍为干净工作区，三仓源码在测试期间未变化。

这次补齐的是自动化传输证据，真实麦克风、ClassIsland／ExamAware2 启停、Windows 自启动、现场配对和班级环境仍未验证。人工、大屏、生产和云端 CI 为 `NOT_RUN`；未提交、推送或部署。
