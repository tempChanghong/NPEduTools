# N3 网页、服务端与桌面投递交付

后续更新：当前验收、原生数据库测试与真实开发电脑闭环见 [N3 收尾记录](NPEP-N3-COMPLETION-20260926.md)。下文 Docker 测试结果是早期记录，最新 `-Database` 已改用临时原生 PostgreSQL。

日期：2026-09-26。三个仓库的本地源码已接通，本轮没有提交、推送或部署，也没有生成桌面发布 ZIP。以下自动化结果不等同于生产环境或真实大屏验收。

## 用户入口与边界

网页：学校管理 → 大屏 → 打开 NPEP 设备互联 → 已登记设备的「考试环境」。页面展示学校、班级、设备、本机许可、最近上报及最近 20 项操作。管理员先核对目标再提交；未授权、离线、录制忙、通知打开、有未解决操作等情况会阻止创建。

N3 只对单台设备请求 `EXAM / CURRENT_RUNTIME`：保留录制计划，暂停后续自动录课，准备 ExamAware2 并验证其桥接就绪，然后正常退出配置中的 ClassIsland。不会停止当前录制，不修改 Windows 自启动，不强杀进程。学校 OWNER／ADMIN 才能发起；设备首次本地许可仍为必需。

网页「请求已登记」仅表示服务端收到了请求。只有设备完成验证并回传 `SUCCEEDED` 才显示「考试环境已就绪」。提交回应丢失时沿用相同 requestId 重试；已经取得开始许可的操作不能远程取消。`PARTIAL`／`UNKNOWN` 保留原结果，现场核实后在桌面结束 N3 暂停，不能用网络超时推断已经回滚。

## 实现位置

| 仓库 | 主要文件 | 职责 |
| --- | --- | --- |
| NPClassworks | `src/components/admin/NpepRuntimeControl.vue` | 单设备面板、确认、状态、取消与历史 |
| NPClassworks | `src/composables/admin/useNpepRuntimeControl.js` | 10 秒刷新、请求幂等、切换设备后的旧响应隔离 |
| NPClassworks | `src/utils/classworksV2Client.js`、`npepRuntimePresentation.js` | 独立 0.3 请求与用户可读状态 |
| NPClassworksKV | `routes/v2/npep.js`、`services/npepRuntimeService.js` | 严格请求边界、权限、许可与执行回执 |
| NPClassworksKV | `services/npepRuntimeRepository.js` | 事务内持久化策略与操作记录 |
| NPEduTools | `NpepRuntime.ControlLoop.cs`、`NpepApi.Control.cs` | 策略上报、任务领取、开始授权、回执重传 |
| NPEduTools | `NpepControlJournal.cs`、`RemoteExamTransport.cs` | 本地投递账本与既有安全执行内核连接 |

命名空间前缀为 `/api/v2/npep`，N3 使用 `X-NPEP-Version: 0.3`。原 N1 使用 0.1、N2 使用 0.2，保持各自接口。

- 设备：`runtime-control-policy`、`runtime-status`、`runtime-operations`、`runtime-operations/:id/start`、`runtime-operation-events`、`runtime-operations/:id/resolve`，均在 `/device/` 下。
- 网页：`/schools/:schoolId/devices/:id/runtime-status`、`runtime-operations` 与操作的 `/cancel`。
- 管理状态新增 `controlEpoch`；网页必须提交其观测到的 consentId、policyRevision、controlEpoch 和三个状态版本，不能自行构造过期授权。
- 服务端按接收时间及采样年龄判断 60 秒新鲜度；申请 5 分钟内可开始，开始许可最长 30 秒。同一申请重试不延长期限。
- 服务端在开始授权前再次检查原发起人的登录会话、有效期及学校角色。学校／设备锁和唯一索引保护单设备未解决操作槽位。
- 桌面在执行前落盘；回应丢失、Host 重启和未解决结果不自动重放软件操作。传输账本不可用时停止 N3 执行；TLS 保持严格验证，失败持续重试。
- 网页当前显示最近 20 项，不开放分页；桌面本地账本最多 256 项，满时拒绝新执行，暂未实现历史归档。

## 数据库变更

后端新增迁移 `20260926000000_npep_runtime_control`，创建 `NpepRuntimePolicy` 和 `NpepRuntimeOperation`，使用 JSONB 保存版本化快照、事件和原始发起人上下文；操作记录包含申请幂等索引和单设备未解决操作的部分唯一索引。不会保存浏览器 JWT。

这两张表保留控制证据，不随设备凭据删除而级联删除。本轮已在独立 Docker PostgreSQL 数据库应用迁移并执行测试，尚未应用到生产。

## 统一测试入口

要求：Windows PowerShell 5.1 或 PowerShell 7、仓库要求的 .NET SDK、Node、三个仓库已还原依赖。入口只调用 Node 与 dotnet，不依赖 PowerShell 7 专属功能。默认约定仓库同级；也可显式指定路径。

```powershell
cd D:\CodeProjects\NPEduTools
./scripts/test-npep-n3.ps1

# 加上真实 HTTP / PostgreSQL / .NET 验证，需安装原生 PostgreSQL，不使用 Docker
./scripts/test-npep-n3.ps1 -Database

# 非同级目录
./scripts/test-npep-n3.ps1 -WebRoot D:\CodeProjects\NPClassworks -BackendRoot D:\CodeProjects\NPClassworksKV -Database
```

入口依次运行：三端契约对齐、网页测试、服务端状态机、桌面执行器与适配器、完整互联回归，最后可选隔离数据库验收。任一步失败则退出。测试所需编译输出放在 `.artifacts/n3-tests`，不会生成发布包或覆盖日常 IDE 的 Debug 输出。数据库脚本创建独立测试项目，结束时清理自己创建的容器及数据卷。

兼容性复核：已将不必要的 PowerShell 7 门槛降为 5.1，并将依赖 `$PSScriptRoot` 的默认路径计算移至参数绑定之后。使用本机 Windows PowerShell 5.1.26100.9444 实际运行默认入口，175 项测试与跨端契约检查全部通过；本次 5.1 复核没有重复执行 `-Database` 分支。

`scripts/check-npep-n3-contract.mjs` 对比后端运行 Schema、后端文档 Schema、桌面嵌入 Schema 及双方共享样例，并直接导入网页 `runtimeCreateBody()` 交给后端校验器验证。它还检查额外路径、自启动、批量控制和 DAILY 请求均被拒绝。桌面真实解析器另行验证同一组 29 个正反例。

本轮结果：

| 验证 | 通过 |
| --- | ---: |
| 网页 API、状态及呈现 | 8 |
| 后端 N3 状态机 | 5 |
| 桌面执行内核及本机适配器 | 54 |
| 桌面 N1／N2／N3 互联回归 | 108 |
| 真实 HTTP／PostgreSQL N1／N2／N3 | 19 |
| 合计 | 194 |

另：29 个共享契约样例、网页真实请求的跨仓库检查、修改过的网页文件 ESLint 均通过。真实数据库场景覆盖同时创建、跨学校拒绝、原发起人降权、开始许可、取消边界、重复事件、部分完成及现场结束后的原结果保留。

浏览器使用实际 Vue／Vuetify 面板，接口为隔离假数据，验证了未勾选不能提交、勾选后提交、排队结果、取消及无页面异常；已检查截图。agent-browser 在本机无法建立浏览器连接，改用已有 Playwright 成功验证。截图位于 NPClassworks 的 `test-results/n3-runtime-ready.png` 与 `n3-runtime-queued.png`。这不是账号登录至真实后端再控制桌面的端到端测试。

## 下一步现场验收

1. 发布配套后端并应用迁移，再发布网页；桌面由 IDE Debug 运行本轮源码。当前生产尚未更新，旧服务端会明确提示未支持 N3。
2. 使用一台测试设备，核对首次本机许可、学校和班级，在网页发起考试环境请求。
3. 核实 ExamAware2 就绪后才正常退出 ClassIsland，网页收到成功回执；检查 Windows 自启动未改变，已有录制不会被停止。
4. 现场结束 N3 暂停，确认原录制计划仍在，并检查网页显示现场结束时间。
5. 用测试设备验证许可关闭、录制忙、回应丢失、部分完成和重启恢复。不要把数据库及模拟执行测试当作这些现场步骤已经完成。
