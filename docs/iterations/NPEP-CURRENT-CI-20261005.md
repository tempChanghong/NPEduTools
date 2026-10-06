# 当前三端组合 CI

本轮只有一个交付：让 NPEduTools、NPClassworks、NPClassworksKV 的当前代码组合接受同一套自动验收。没有准备新版本，也不涉及 NPEssentials。

## 运行机制

| 入口 | 受测提交 |
| --- | --- |
| 任一仓库的 PR | 该 PR 的准确 head SHA，另两端在任务开始时解析的 main SHA |
| 任一仓库 main 的 push | 本次提交 SHA，另两端在任务开始时解析的 main SHA |
| 手动运行 | 可输入三端完整 40 位 SHA；未输入的调用仓库使用本次提交，其他仓库解析 main |

解析只执行一次。随后的 checkout、来源检查、验收和报告使用同一组 SHA。解析失败、来源不符或 CI 目录存在未提交改动都会失败，不退回旧版本。PR／push 不能通过传入另一 SHA 替换自己的受测提交。

旧的 `npep-pairing.yml` 与 `npep-pairing-ci.json` 保持原样，继续承担固定版本的配对兼容性检查；新增的 `npep-current.yml` 标为 **CURRENT**，两类结果不能混用。

验收驱动与三个受测 checkout 分开。手动回放历史源码时仍使用当前驱动；报告额外记录驱动 SHA 及是否有本地改动，避免把驱动代码误当成受测产品代码。

## 验收内容

- 配对、凭据边界、网页请求与后端契约。
- 远程考试与返回日常、持久化请求重试、考试方案。
- 原生噪音状态、结束报告、定时监测、显示保护、在线状态与恢复。
- 当前 C# 生成的定时监测／管理／在线状态样例与网页、KV 的交叉检查。
- 真实 Vue／Vuetify 浏览器中的配对、噪音、定时页面，以及状态过期、切设备与错误提示恢复。
- 原生 PostgreSQL 临时集群中的迁移、数据库规则和真实 .NET／HTTP 交换。

浏览器请求和数据库都在隔离的 loopback 环境；音频、桌面执行和 Guard 进程测试使用合成数据或测试替身。不会连接生产服务、访问真实麦克风、改变 Windows 自启动或操作已安装的大屏应用。

## 本地入口（Windows PowerShell 5.1）

需要 Node.js 22.18+（22 LTS）或 24+、.NET SDK、两个 JS 仓库已安装的冻结锁文件依赖；完整验收还需 Playwright Chromium 与原生 PostgreSQL 工具。先执行 `pnpm install --frozen-lockfile` 和网页目录中的 `pnpm exec playwright install chromium`。22.18 之前的 Node 22 默认不能直接加载生成的 Prisma TypeScript 客户端，入口会提前指出版本问题，而不是在后端测试中报不明文件扩展名。[Node 官方说明](https://nodejs.org/en/blog/release/v22.18.0)

在 NPEduTools 根目录运行（按实际目录调整）：

```powershell
./scripts/test-npep-current.ps1 `
  -WebRoot D:\CodeProjects\NPClassworks `
  -BackendRoot D:\CodeProjects\NPClassworksKV `
  -Browser -Database
```

默认会记录当前实际提交和未提交改动，属于本地验证。CI 额外传入三个完整提交、`-RequireClean` 和解析清单，必须使用三个干净 checkout。

来源检查以 Git 规范化内容、暂存区及未跟踪文件为准。Windows 自动生成文件仅发生 CRLF／LF 格式转换且 Git 内容相同时，不算代码改动；真正的内容或暂存改动仍被拒绝。检查本身不修改文件或索引。

结果保存在驱动仓库的 `.artifacts/npep-current/runs/<随机编号>/`，包含 `result.json`、测试前后的 `sources*.json`、阶段日志和 TRX。定时监测的详细子结果在受测桌面仓库 `.artifacts/noise-schedule-tests/`。

只有同时选择 `-Browser -Database` 才标为 `FULL_AUTOMATED`；省略选项时为 `SELECTED_AUTOMATED`，对应阶段记为 `SKIPPED`。`PASSED` 仅指该报告列出的自动验收通过；真实麦克风、班级大屏、部署状态始终单独记为 `NOT_RUN`。

## 接入顺序

1. 先单独提交 NPClassworks 的 `scripts/test-native-noise-browser.mjs` 修正，暂不提交它的调用 workflow。旧断言把报告中已经分开的标签、数值当作一个元素，当前入口将发现它并失败；修正按会话定位报告，逐项核对采样时长、总时长与覆盖率。
2. 该修正通过现有 CI 并合并后，提交并审核桌面仓库的驱动、测试与 reusable workflow，先合并到 NPEduTools main。
3. 再为 NPClassworks 与 NPClassworksKV 提交调用 workflow；它们通过桌面仓库的 `@main` 入口调用同一驱动。
4. 检查各份 PR／main 的 GitHub CI，核对摘要与上传的实际 SHA，再决定后续合并。

网页、KV 调用文件在第二步完成之前不可用，因为远端 main 尚不存在该 reusable workflow。不要把第一步的网页测试修正与调用文件提前放进同一个 PR。当前准备的本地文件并不表示 GitHub CI 已执行，也不表示已部署。

参考：[GitHub reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)。调用流程保留调用方 GitHub 事件上下文，只需 `contents: read`，不继承部署密钥。

## 本轮验证记录

2026-10-05 本地完整入口通过：Windows PowerShell **5.1.26100.9444**、Node **22.23.3**、.NET SDK **10.0.401**。结果为 `PASSED / FULL_AUTOMATED`。

| 检查 | 结果 |
| --- | --- |
| 来源选择与失败结果回归 | 8 项通过；包含三类真实 PowerShell 失败路径 |
| 三份 workflow 的 actionlint 1.7.7 检查 | 通过 |
| 桌面完整 NPEP 传输／恢复 | 182 项通过 |
| 桌面考试适配／引导 | 184 项通过 |
| 网页／KV 的配对、考试、噪音与共享契约 | 通过 |
| 定时监测、显示／保护／在线状态、Guard 测试替身 | 通过 |
| 浏览器配对、原生噪音、定时编辑／报告页面 | 通过；浏览器麦克风调用为零 |
| 完整应用浏览器页面 | 定时显示 1 项、状态／错误恢复 7 项通过 |
| 临时 PostgreSQL 升级与 HTTP／.NET 交换 | 升级 6 项、数据库／HTTP 45 项通过，无跳过；临时集群已清理 |
| 测试后的三个 HEAD 核对 | 通过 |
| GitHub 当前组合 workflow | 尚未提交，未执行 |
| 真实班级大屏、麦克风、生产部署 | 未执行 |

本次受测基线：桌面 `90ec7be8366e3d3a6b0d08400c7b942d417ba105`、网页 `a5ce94fcb7cbd2124ba17bab8b48aff031206d16`、KV `c7f6ead2c3bb39d0a83e094e20c1b50a2c1c956f`。包含本轮未提交的 CI／测试改动，报告明确记录 `immutable: false`、`localChanges: true`；不能将本机结果表述为已通过 GitHub 的干净提交组合。

本机主结果目录：驱动仓库 `.artifacts/npep-current/runs/db011f1f444549188a4aefb81d016533/`；定时监测子结果为 `.artifacts/noise-schedule-tests/run-e3ad42bc16ad478e8d06e36649871aad/`。此前发现旧报告断言失效及低于要求的 Node 22 版本问题，失败结果仍保留；最终上述完整入口已通过。

### 2026-10-06 提交前复核

在网页学生手机布局修正后的当前源码上重新运行完整入口，16 个阶段再次得到 `PASSED / FULL_AUTOMATED`。受测提交：桌面 `90ec7be8366e3d3a6b0d08400c7b942d417ba105`、网页 `174d9f6c1d77decbfd7d59ffebade7fa3a08f1ee`、KV `c7f6ead2c3bb39d0a83e094e20c1b50a2c1c956f`。本轮 CI 文件尚未提交，仍是 `immutable: false` 的本地结果；真实设备与部署验收未执行。

主结果：`.artifacts/npep-current/runs/9b9e8b96296b43e489b9c5a6331ab2a0/result.json`。来源回归 8 项、桌面传输 182 项、考试／引导 184 项、数据库升级 6 项和 HTTP／数据库 45 项均通过；浏览器与定时保护阶段通过，无跳过，临时数据库已清理。

网页独立测试修正 [NPClassworks PR #9](https://github.com/tempChanghong/NPClassworks/pull/9) 的 Frontend tests（单元／Lint、浏览器、契约／数据库）及 PWA Store Build 均通过后合并，主分支提交为 `8133ac98b1f1b6c9a260a84a446afa5010b457ac`。可选 Claude review 按已有配置跳过，不计作测试通过。后续桌面入口将在 GitHub 使用这个网页 main 与 KV main 验证准确的干净源码组合。
