# NPEP N1 Docker 与 CI 接入记录

日期：2026-09-21。本轮按用户要求补齐生产 Docker 配置和 N1 检查；所有修改留在功能分支。用户已明确 main 会触发 server.js 自动部署，因此本轮没有合并、推送、调用部署代理或改变线上配置。

## 设备端 Windows CI

新增 `.github/workflows/npep-n1.yml`，在 PR、main/codex 分支推送及手动触发时运行。job 为 `N1 device and Host regression`；只读仓库权限，checkout 不持久化凭据，没有发布步骤。其入口与本地一致：

```powershell
./scripts/test-npep-ci.ps1
```

检查包括：

- 从 `global.json` 选择 SDK，NuGet `--locked-mode` 还原；不自动更改依赖版本。
- 41 个 wire schema 正反例。
- Release 构建 App（包含 Host）、NpepProbe 和 HTTPS 验收工具。
- NPEP 专项与原有回归；用 TRX 逐项确认所有测试已执行且通过。空报告、缺报告、失败、跳过，以及汇总与逐项结果不符均失败。
- 每次使用新结果目录，记录源提交、工作区是否有修改、PowerShell 版本与完成状态。GitHub 仅上传 TRX 和 run.json，不上传测试凭据目录或生产资料。

设备代码提交 `6d5e0c6`。先在原 C 盘测试副本运行，再在没有旧 bin/obj 的独立干净 worktree 复跑；第二次结果中的 `dirty=false`、`completed=true`，SDK 10.0.400、PowerShell 7.6.5。仅复用 NuGet 包缓存，没有复用旧编译输出。

| 检查 | 本机结果 |
| --- | --- |
| N1 结构示例 | 41/41 |
| NPEP 专项 | 86/86，零跳过 |
| 原有回归 | 352/352，零跳过 |
| 三个构建入口 | 全部通过，零警告、零错误 |
| TRX 拒绝错误结果 | 有效、空、跳过、失败、汇总造假、缺逐项、缺文件共 7 个场景符合预期 |
| workflow YAML | 解析和触发器/权限/job 检查通过 |

这是本机执行与工作流配置验证，**还不是 GitHub 托管 runner 的成功运行记录**。工作流须在功能分支推送后运行；仓库分支保护也未被本轮自动修改。此 Windows job 不声称覆盖真实学校 TLS、生产数据库或大屏人工现场验收。

SDK setup 的 `global-json-file` 用法已对照 [actions/setup-dotnet v4 官方说明](https://github.com/actions/setup-dotnet/blob/v4/README.md)；Windows runner 软件说明见 [官方 Windows 2022 镜像清单](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)。运行时仍检查最低 PowerShell 7.5，不依赖静默跳过不兼容环境。

## 服务端与网页配合

NPClassworks/KV 任务完成两份 Compose、容器权限、配置初始化、恢复前保护和网页全链路检查接入：

- 两份 Compose 默认关闭 NPEP，将独立命名卷 `npep-config` 挂到 `/var/lib/npclassworks-npep`；镜像预建 node 用户拥有的目录。启动先检查配置与写权限，再迁移和运行服务。
- `init` 创建关闭的身份配置并拒绝覆盖已有文件；启用环境开关不会自动激活。激活仍是独立运维步骤。
- restore 无条件比对宿主/容器开关并关闭旧入口；双方都关闭但存在历史身份时也轮换代际。缺脚本、不一致或无法写入时，在停止服务/删除数据库前失败。旧恢复 CLI 也转到同一逻辑，不再通过静默成功绕过。
- 前端 contracts 与后端生产 fullstack 显式要求 `pnpm test:e2e:npep`，检查前后端都有 N1 实现；空、跳过、失败或重试后才通过均阻止门槛通过。
- 后端纯测试 quality 与生产 verify 都要求 `pnpm test:deployment:npep`，使用随机命名测试镜像和卷验证实际容器用户、挂载、原子写和拒绝错误配置。负例必须返回预期退出码与错误信息，Docker 命令自身失败不能充当通过。

设备任务独立审阅差异，并以纯测试 env 文件解析两份生产 Compose 的开关 false/true 四种组合：独立可写命名卷、固定容器路径、与 PostgreSQL 卷分离均符合预期，没有启动生产 Compose 或读取生产 env。

具体使用步骤与服务端执行结果以 [分离部署准备记录](../../../NPClassworksKV/docs/NPEP-N1-DEPLOYMENT-READINESS.md) 为准。首次发布需要前后端都具有 N1；仅一端合 main 时，另一端尚不支持 N1，应阻止部署，不能跳过专项用例。

检查中发现的激活脚本导入遗漏已修正；恢复 CLI 的旧旁路和容器负例只检查“非零退出”的验证缺口也已闭环。

服务端任务的最终验证记录如下；这些是对端执行结果，设备任务未重复启动其数据库或容器套件：

| 检查 | 结果 |
| --- | --- |
| NPClassworks 单元与 lint | 513/513、全量 lint 通过 |
| 新的强制 N1 网页门槛 | 真实浏览器/PG 1/1，零跳过、零重试通过；账号会话前置用例 1/1 |
| KV 默认单元命令 | 187 通过，17 个数据库用例在默认环境跳过；不把这些跳过计为数据库验收 |
| KV 完整真实 PG 回归 | 113/113、零跳过，含实际激活、pg_restore、两种部署模式开关不一致时原数据保留 |
| 生产 Dockerfile 与容器门槛 | 构建及默认关闭、非 root 用户、持久化目录、原子写、只读/不可写/缺配置等检查通过 |
| 旧恢复 CLI 与工作流格式 | 旧入口定向测试和 3 份修改后的 workflow YAML 检查通过 |

本地功能分支提交：

| 仓库 | 分支 | 本轮代码提交 |
| --- | --- | --- |
| NPEduTools | `codex/npep-n1-ci` | `6d5e0c6`（Windows CI；本文等记录随后提交） |
| NPClassworks | `codex/npep-n1-admin-ui` | `e354bbd` |
| NPClassworksKV | `codex/npep-n1-server` | `88134b1` |

两端工作区已确认干净。服务端任务清理了本轮专用测试项目、卷和镜像，没有触碰既有共享 PostgreSQL。设备侧保留 C 盘干净测试 worktree 和 ignored 的 TRX，便于追溯；无持续运行的测试任务。没有重新执行上轮 26 项设备 HTTPS 验收，本轮没有修改设备运行实现。

目前部署代理仍会解析另一端 main；本轮未修改 `server.js`。因此成对 CI 的 SHA 记录证明的是测试组合，不应宣称代理已经部署了这个精确组合。发布窗口应固定两端版本，并核对部署后的版本清单；这项线上操作仍需单独审核。

## 审核与现场边界

当前线上 API 404 的已知背景仍是功能分支尚未发布；本轮未再次访问或写入线上。网页地址为 `https://newfires.top`，将来的设备 API origin 为 `https://api.newfires.top`。

下一阶段应审核三个功能分支及不可变提交，先推功能分支取得托管 CI 结果，再审核 main 的成对发布顺序。发布后保持 NPEP 外部配置关闭，核对迁移、卷与恢复入口，再单独进行初始化/启用和真实大屏试点。不要把 main 合并当成没有生产影响的普通整理操作。

现场配对、收起窗口、真实网卡断线、休眠和 Windows 重启继续按 [现场验收表](NPEP-N1-FIELD-ACCEPTANCE.md) 记录。N1 仍仅为 `device.status`，没有新增通知、远程切换模式或录制控制。
