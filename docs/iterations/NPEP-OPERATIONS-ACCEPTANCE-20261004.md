# NPEP 运维验收统一入口 · 2026-10-04

| 任务卡 | 内容 |
| --- | --- |
| 主交付 | 概览和诊断摘要的一条命令验收，复用已有原生测试能力 |
| 范围 | 三仓源码基线、N1 契约一致、实际 .NET 合成状态经后端／网页投影、针对性回归、WPF 编译；可选浏览器及临时 SQL |
| 边界 | 不运行真实大屏软件或麦克风，不安装应用，不调用 Docker、不连接调试／生产数据库，不部署，不改 NPEssentials |
| 验收 | PowerShell 5.1／7 入口、零退出码但空／跳过／异常结果拒绝通过、错误分项落盘、未选阶段明确标记、源码期间变化拒绝通过 |

后续已补齐 [摘要对照](NPEP-DIAGNOSTIC-COMPARISON-20261004.md) 和 [完整 .NET 传输联调](NPEP-OPERATIONS-TRANSPORT-20261004.md)。下文使用说明描述当前入口；“本轮结果”保留最初实现的历史数据。

## 怎么运行

在 NPEduTools 根目录：

```powershell
.\scripts\test-npep-operations.ps1 -CheckOnly
.\scripts\test-npep-operations.ps1
# 已安装项目依赖、Playwright Chromium 和原生 PostgreSQL 时：
.\scripts\test-npep-operations.ps1 -Browser -Database
```

默认读取同级 `NPClassworks`、`NPClassworksKV`；目录不同时传 `-WebRoot`、`-BackendRoot`。兼容 Windows PowerShell 5.1，执行逻辑使用项目已有 Node.js 环境（本轮 Node 24、.NET 10）。不会自动安装依赖或浏览器。`-CheckOnly` 只检查目录、依赖、SDK 和 Schema，不会执行测试、启动应用或数据库；结果为 `CHECKED`，不是 `PASSED`。

CI 另传 `-RequireClean -DesktopCommit <完整SHA> -WebCommit <完整SHA> -BackendCommit <完整SHA>`，拒绝未提交改动和不匹配的源码组合；默认本地命令仍允许开发中的改动。工作流和来源登记见 [CI 任务卡](NPEP-OPERATIONS-CI-20261004.md)。

默认检查：入口自己的失败回归、桌面诊断／共享互联会话／无探测缓存读取、真实 C# 状态映射的四个合成跨端用例、隔离目录 WPF 编译、后端概览／N1 协议与错误映射、网页摘要与概览及管理员生命周期回归。

`-Browser` 复用隔离测试服务，自动选择两枚临时回环端口，使用 `NODE_ENV=production` 构建，避免忽略的开发环境地址抢占测试服务。运行三条 NPEP 管理流程，包括实际 JSON 下载／过时标注、权限清空、旧服务兼容和迟到响应。

`-Database` 复用 `run-native-npep-tests.js --desktop-root <本次桌面源码目录> --test-reporter tap`：编译该目录的 .NET 验收程序，新建临时 PostgreSQL 集群，执行真实 SQL／HTTP、旧配对迁移以及五类 .NET 传输联调，成功后停止并清理。不再传 `--skip-desktop`；选择数据库阶段就要求所有测试零跳过，五类传输场景和概览 SQL 子用例各有实际通过记录。真实配对凭据仅属于隔离测试设备；系统启停、自启动设置、放映、麦克风与学校时钟使用测试替身，不操作用户软件。

KV 的原生测试脚本新增可选 `--test-reporter tap|spec` 参数；直接使用时保留原来的默认输出，数据库、迁移和清理逻辑不变。没有修改后端业务接口。

## 结果怎么读

每轮路径：`.artifacts/npep-operations/<时间>-<随机ID>/result.json`。入口打印 `NPEP_OPERATIONS_RESULT:`；同目录含分项 stdout／stderr 日志、TRX、浏览器 JSON、合成 .NET 数据与跨端契约结果。

- `PASSED`：所选阶段全部执行并有有效证据。Node 要有实际 TAP 通过计数，桌面要有无跳过 TRX，浏览器要有实际成功且无跳过／重试波动的概览下载流程。
- `CHECKED`：仅预检查；所选测试仍为 `NOT_RUN`。
- `NOT_REQUESTED`：没有选择可选浏览器／数据库检查，不能算通过。
- `FAILED`：预检查、命令、证据或源码稳定性失败；剩余测试停止，日志保留。
- `RUNNING`：执行中或被强制中止。不是成功；已结束的子项也不能代表整轮通过。

报告记录三仓 HEAD、是否存在本地改动，以及受检源码／测试／配置文本输入 SHA-256 清单（包含 C#、JavaScript、Vue、XAML、JSON、项目／锁文件、SQL、HTML、样式和测试脚本等；详见 `source-inputs.json`）。默认允许工作区改动，但不会标为不可变发布组合。测试前后对比受检输入和 HEAD，期间变化要求重跑。忽略的本机密钥、构建产物不遍历；清单只含路径与哈希，不含源文件正文。不涵盖二进制资源及所有文档，不是安装包签名或完整发布产物证明。

本入口的 `releaseReady=false`、人工／真实大屏／生产／部署均为 `NOT_RUN`。通过后仍需具体设备验收，不能替代整套 NPEP 或发布审核。只维护本任务所涉的验收，不替代 N4、考试方案、安装包各自的完整入口。

## 本轮结果

Windows PowerShell `5.1.26100.9444` 完整执行 `-Browser -Database`：`PASSED`，`sourceStable=true`。Node `24.14.1`、.NET SDK `10.0.401`、原生 PostgreSQL 17；PowerShell 7 的 `-CheckOnly` 另行通过，未在 PowerShell 7 重跑完整测试。

| 分项 | 结果 |
| --- | --- |
| 入口与证据校验回归 | 8 通过 |
| 桌面针对性回归 | 34 通过 |
| 实际 C# 数据跨端检查 | 4 个合成状态通过 |
| 隔离 WPF 编译 | 通过；未启动应用 |
| 后端针对性回归 | 20 通过 |
| 网页单元／生命周期回归 | 5 + 7 通过 |
| 浏览器 NPEP 管理流程 | 3 通过，无跳过或重试波动 |
| 临时 SQL／HTTP 集成 | 40 通过，5 项按范围跳过 |
| 旧配对迁移检查 | 6 通过；临时集群清理已确认 |

五项跳过分别为噪音鉴权、N3、N4、网页配对与重启撤销、N4.3c 调度的实际 .NET HTTP／PostgreSQL 联调；本入口使用 `--skip-desktop` 有意排除它们。桌面单元回归和四个跨端合成用例不能替代这些完整联调。

完整记录：`.artifacts/npep-operations/2026-10-04T09-58-01-643Z-ce755989/result.json`；PowerShell 7 预检查记录：`.artifacts/npep-operations/2026-10-04T09-59-34-158Z-c1647d3c/result.json`。

接入时发现并修正两处测试入口问题：浏览器构建继承 `NODE_ENV=test` 后误用开发服务地址；Node 24 默认原生测试输出为 spec，证据读取需要显式 TAP。失败轮次的分项日志保留，修正后重新完整执行通过。

人工、真实大屏及生产验收未进行。未提交、推送或部署。
