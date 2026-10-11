# NPEP 运维验收接入 CI · 2026-10-04

后续统一入口已要求 [五类完整 .NET 传输联调](NPEP-OPERATIONS-TRANSPORT-20261004.md) 零跳过，现有 CI 命令自动沿用该要求。以下本轮结果保留最初 CI 接入的历史验收，不能代表更新后的云端运行结果。

| 任务卡 | 内容 |
| --- | --- |
| 主交付 | 三仓固定提交组合的 GitHub 运维验收 |
| 范围 | PR／手动／复用工作流、完整 SHA 来源登记、干净源码检查、同一本地入口、限定证据上传 |
| 边界 | 不推送、部署或发布；不改变业务接口、真实设备状态；不改 NPEssentials |
| 验收 | 拒绝分支、短 SHA、提交不匹配、未提交文件；干净检出运行浏览器和临时原生 PostgreSQL；工作流语法检查 |

## 如何使用

工作流为 **NPEP three-repository operations acceptance**。提交并推送配置后，面向 `main` 的 NPEduTools PR 自动运行；也可在 Actions 手动运行或由其他工作流调用。

默认测试桌面 PR 的 head 提交（手动运行默认工作流提交），前后端使用 `scripts/npep-operations-ci.json` 登记的完整提交编号。手动运行可覆盖三个 SHA；不接受分支名或短编号。前后端新改动需要先提交并推送，再更新登记文件，避免暗中跟随其他仓库的最新分支。

当前登记：NPClassworks `64d97756f9a3ea6694a335b5b1302f7d67750f12`；KV `bd6203c6cb9141f117a457eae385723274d4f07e`。本轮这些提交尚未推送，远端未必可检出；配置完成不等于云端 CI 已通过。

CI 在临时 Windows runner 安装依赖和原生 PostgreSQL 工具，然后调用：

runner 检出前关闭其全局 `core.autocrlf`，统一使用仓库文本字节；不修改维护者电脑的 Git 设置。Vite 会重写自动导入配置为 LF，如果初始检出为 CRLF，即使内容相同，严格字节校验也会拒绝通过。本轮统一检出规则，而不排除该文件或事后还原来掩盖变化。

```powershell
.\scripts\test-npep-operations.ps1 -Browser -Database -RequireClean `
  -DesktopCommit '<40 位桌面 SHA>' -WebCommit '<40 位网页 SHA>' -BackendCommit '<40 位后端 SHA>'
```

本地继续使用原命令即可，不强制清理开发中的改动。`-RequireClean` 要求三份仓库都提供准确完整 SHA，并在运行前后重新核对干净状态。缺失依赖、前后端代码尚未推送或源码生成改变受检文件都会失败，不会改用旧提交或略过测试。

## CI 显示什么

只读 `contents` 权限；三个 checkout 不保留 Git 凭据，不使用部署密钥、生产地址、生产数据库或 `pull_request_target`。失败后仍尝试上传本轮限定路径的 JSON、TRX 和分项日志，保存 14 天；不上传源码、编译目录、`.env` 或数据库目录。浏览器使用合成学校和隔离本地服务。

PR 检查对应指定 head 与两份配套提交，不宣称测试了 GitHub 临时合并提交。前后端仓库自己的 PR 不会自动触发这个新工作流，需更新桌面登记文件或显式调用。既有配对 CI 保留原范围和来源登记，不把本轮运维检查当作全部 NPEP 验收。

规则依据：[GitHub 工作流事件](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)、[工作流权限与输入语法](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)。

## 本轮结果

本轮先提交上一轮的运维概览、诊断与验收代码：NPEduTools `7fc2a4d`、NPClassworks `64d9775`、KV `bd6203c`。本任务的 CI 配置和严格来源检查仍在桌面工作区，未提交／推送。

| 验证 | 结果 |
| --- | --- |
| 工作流语法 | actionlint 1.7.7 通过；另核对只读权限、三个固定引用 checkout 和限定上传路径 |
| 入口／来源回归 | 10 通过，包括三个真实临时 Git 仓库的提交不匹配、缺失 SHA、未跟踪文件和目录混用拒绝 |
| 干净检出的完整验收 | PowerShell 5.1 下 `-Browser -Database -RequireClean` 通过，`immutable=true`、`sourceStable=true` |
| 桌面／跨端／编译 | 34 桌面测试、4 个 C# 合成状态跨端检查及隔离 WPF 编译通过；未运行应用 |
| 网页／后端 | 后端 20、网页单元及流程 12、浏览器 3 通过 |
| 隔离原生数据库 | SQL／HTTP 40、迁移 6 通过；五项 .NET 完整传输联调按 `--skip-desktop` 跳过，清理确认 |
| PowerShell 7 | 干净来源 `-CheckOnly` 通过；没有在 PowerShell 7 再运行完整检查 |
| 云端／人工／大屏／生产 | `NOT_RUN`；无推送或部署 |

验证使用本地新克隆，不带开发环境 `.env` 或未提交的安装器／录课改动；桌面检出中仅覆盖本任务六份代码／配置文件，并创建临时测试提交 `ae799697f67a183a24299f1bd9c163b8b0906750`。该提交只属于 `.artifacts` 中的测试克隆，不是桌面正式分支提交或发布候选。前后端检出与上述登记 SHA 一致。

完整报告：`.artifacts/operations-ci-local/20261004-b7f1/NPEduTools/.artifacts/npep-operations/2026-10-04T10-16-49-748Z-c7ebeebb/result.json`。PowerShell 7 预检查报告同目录的 `2026-10-04T10-17-52-833Z-fff25cf7/result.json`。

第一次干净检出的业务测试均通过，但最终严格检查正确拒绝了 Vite 引起的 CRLF → LF 字节变化；失败记录保留在 `.artifacts/operations-ci-local/20261004-a6e0`。统一检出换行规则后重新运行全部阶段通过。原开发工作区 `-RequireClean` 也按预期拒绝了已有未提交改动，未启动测试或数据库。

五项跳过仍为噪音鉴权、N3、N4、网页配对／重启撤销及 N4.3c 调度的实际 .NET HTTP／PostgreSQL 联调；桌面针对性测试不替代这些集成场景。
