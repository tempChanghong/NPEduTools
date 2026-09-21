# NPEP N1 首次发布方案与用户待办

日期：2026-09-21。状态：代码验证及 GitHub 受控快进已完成，等待河豚豚执行服务器升级；NPEP 尚未启用。此次范围是 N1 配对、撤销与只读状态；通知、考试模式远程切换和录制控制仍未开放。

## 23:00 更新：首次上线未就绪，已自动回退旧应用

河豚豚已执行升级：镜像构建和前端 PWA 校验通过，但新后端就绪检查失败；脚本自动回退旧应用并报告回滚完成，没有数据库恢复操作。Codex 随后实测公网 API /ready 和网页均 HTTP 200。数据库是否已应用 N1 迁移尚未确认，不重跑升级、不盲目恢复数据库。GitHub main 保持新 SHA，线上应用按日志回到旧版本，三条发布 workflow 与 PM2 继续暂停。下一步只读诊断，详见 [首次部署事件记录](NPEP-N1-FIRST-DEPLOY-INCIDENT.md)。下方成功前的升级交付步骤暂停，不能作为重跑许可。

## 22:49 最新状态：main 已就位，服务器待升级

河豚豚已回报 PM2 `np-deploy-agent`（ID 4）stopped。Codex 再次验证三条发布入口仍禁用、CI 成功、分支未漂移及祖先关系后，通过 GitHub API `force=false` 顺序快进 main：后端 `e660876c8a22004e14a053a70985d5b350e42d0d`、前端 `37a3f1b01585fd8cfc751c921668a84d5c41264f`。没有额外合并提交，实际内容与受测 SHA 一致。快进后再次核查发布入口仍禁用且无未完成的发布任务。

已向河豚豚交付 [受控发布操作记录](NPEP-N1-CONTROLLED-RELEASE.md) 中第二段固定版本升级命令。命令先核验服务器旧 HEAD、shared、NPEP=false、PM2 stopped、远端 main 和运行镜像标签，再调用现有升级脚本，并把输出与回滚状态单独留存到 `/root/npclassworks-release-*`。现在等待现场执行结果，Codex 未直接连接服务器或发起部署请求。暂停前控制状态、升级前置核对、main 结果均有 JSON 记录。下方旧阶段描述保留历史，当前状态以本节为准。

## 22:35 发布控制更新

用户已明确授权开始处理。Codex 已保存原状态并暂停三个 GitHub 工作流：前端 production-deploy（345174965）、后端 production-deploy（345174964）、后端 docker-publish（330817952），均从 active 变为 disabled_manually。暂停后复查两仓所有在途状态均为空，部署代理 22:35:13 返回 `ok=true,busy=false,queued=0`。尚未合并 main、停止 PM2、升级网站或开启 NPEP。

当前交接给河豚豚的唯一操作是：重新核对代理空闲后，使用同一 root/PM2 会话停止 `np-deploy-agent` 并回报 stopped。完整顺序、已保存的 GitHub 状态和升级/回退草案见 [受控发布操作记录](NPEP-N1-CONTROLLED-RELEASE.md)。收到 stopped 之后才处理 main；不提前发起生产更新。下方“未停用工作流”等初轮描述属于此前阶段，当前以本节和控制记录为准。

## 最终候选与纯测试进度

河豚豚现已直接参与本对话。N1、图标和最小辅助脚本修复已收敛，两工作区干净。根任务审计触发条件后，仅推送功能分支并发起以下纯测试；未合并 main、发布镜像或请求部署：

| 项目 | 最终候选 | 当前验证 |
| --- | --- | --- |
| 前端 | `codex/npep-n1-admin-ui` / `37a3f1b01585fd8cfc751c921668a84d5c41264f` | [Frontend tests 35608546438](https://github.com/tempChanghong/NPClassworks/actions/runs/35608546438)，success；其中初次联调的后端为 `3db912d...`，前端独立单元与浏览器结果仍适用 |
| 后端 | `codex/npep-n1-server` / `e660876c8a22004e14a053a70985d5b350e42d0d` | [修正后 Quality 35609934460](https://github.com/tempChanghong/NPClassworksKV/actions/runs/35609934460)，success |
| 修正后固定组合 | 前端 `37a3f1b01585fd8cfc751c921668a84d5c41264f` + 后端 `e660876c8a22004e14a053a70985d5b350e42d0d` | [Contracts 35609940329](https://github.com/tempChanghong/NPClassworks/actions/runs/35609940329)，success；两个输入均为完整 SHA |

前端 tests.yml 仅增加手动运行 backend_ref 输入并传入现有 reusable workflow，PR 默认后端 main 不变；生产工作流未修改。图标相关全量 lint、资产映射验证及 YAML 输入传递本地检查已通过，托管最终检查也已全部通过并核对日志与产物。两仓 main 经 API 再查仍分别为 `19756f654f94006084d1d954b8be4171db7c9a18` / `f731f3227a7c59585aff940f78354585d3b016b7`。未向 PM2 发出任何操作，部署代理仍按最后回报在线。本阶段不提前暂停。下方此前基线审查表保留作历史证据，不能代替这组最终提交的验证。

初轮后端 `3db912d60f9b3db2798ce937c4d23ce0d77b3706` 的 [Quality 35608552240](https://github.com/tempChanghong/NPClassworksKV/actions/runs/35608552240) 失败，已下载日志核对：单元测试与 Docker/恢复门槛步骤通过；独立 PostgreSQL 测试中的 shared 备份恢复子用例在 `backupRestoreDatabase.integration.test.js:135` 被“同名备份已经存在”保护拒绝，汇总 111 pass / 2 fail（子用例及其父套件）/ 0 skip。此失败发生在 GitHub 隔离测试，不是河豚豚的生产备份或刚完成的恢复演练失败。

根因修复分别提交为 `b9f12c037272ae4397060eade635912dde4a8f04` 和 `e660876c8a22004e14a053a70985d5b350e42d0d`：备份保留可读时间与标签，改用 mktemp 原子预留随机文件名，校验后以同目录硬链接原子发布、拒绝覆盖；加入固定同秒的真实两模式恢复回归。另发现主机与 Docker 数据库时钟存在约 1.4 秒以上偏差，将维护测试的过期数据改用 PostgreSQL 自身时间构造并断言，生产清理逻辑不改。修正版在 C 盘精确副本验证完整 PG 113/113、零跳过，部署检查 15/15 及原子目标碰撞保护通过；D 盘 Docker bind mount 的环境错误未通过重启共享服务处理。备份目录需支持同目录硬链接，失败不会降级为覆盖。新 SHA 已推送功能分支，上方托管复核已通过。

前端最终运行已读取三个 job 日志：513/513 单元、全量 lint、150/150 浏览器均通过。后端新 SHA 的 quality 和前后端固定组合 contracts 均已追加完成：KV 默认单元 187 pass / 17 数据库预期 skip，独立 PostgreSQL 113/113 零跳过，生产 Docker 门槛通过；固定组合 3/3 契约、37 通用全链路通过及 1 预期跳过，随后强制 N1 1/1 通过，两次会话前置检查各 1/1。下载的两份 versions.json 都对应上表最终完整 SHA，dirty=false；N1 结果 expected=1、skipped=0、unexpected=0、flaky=0。前端独立代码未改，未重复已通过的单元与浏览器检查。

用户后续已确认：服务器由河豚豚操作，前端路径 `/NPClassworks`、后端路径 `/NPClassworksKV`，维护时间灵活、配置在默认路径。首次备份、数据库隔离恢复和异地备份现已完成。部署代理为 PM2 `np-deploy-agent`（ID 4，online），未暂停。用户已同意先关闭状态更新、再单台大屏启用的路线。核查记录见 [服务器操作者核查单](NPEP-N1-OPERATOR-HANDOFF.md)，结果见 [首次备份记录](NPEP-N1-FIRST-BACKUP.md) 和 [隔离恢复记录](NPEP-N1-RESTORE-DRILL.md)。

## 结论

已同意分两次推进：**先发布默认关闭的 N1 代码，验收原有业务；再安排一台真实大屏启用试点。** 不应现在直接点击两个仓库的合并按钮。两端 main 会自动触发生产部署；已取得现场仓库版本与代理空闲快照，实际镜像版本和回退材料仍待核实，部署入口仍需受控。

尚未合并或推送 main，没有修改 GitHub 环境保护、连接服务器、改变生产开关或进行配对。已推送前后端功能分支并运行上方纯测试，随后按用户授权暂停三条发布工作流；本仓发布记录保存在功能分支本地，未因文档变更触发生产发布。

## 已完成的核查

| 项目 | 本次结论 |
| --- | --- |
| NPEduTools 分支 | `codex/npep-n1-ci`，审查时 HEAD `5771214733e4b24f5efca0465ec72838cce1611a`；[该提交的 Windows 检查通过](https://github.com/tempChanghong/NPEduTools/actions/runs/35563152422)，实现代码与此前 `0d9719c` 相同 |
| 前端分支 | `codex/npep-n1-admin-ui`，受测代码 `e354bbd172190a8a201120d1da8356abfc9e7a33` |
| 后端分支 | `codex/npep-n1-server`，受测代码 `88134b175e7f95055aacdb525b8b10dee1e6bc35`；本轮服务器审查材料单独提交，不改变运行代码 |
| 配套检查 | [前后端固定组合通过](https://github.com/tempChanghong/NPClassworks/actions/runs/35561798752)，[后端 Docker/PG 检查通过](https://github.com/tempChanghong/NPClassworksKV/actions/runs/35561797164)；具体执行数量见 [托管记录](NPEP-N1-HOSTED-CI-REPORT.md) |
| 合并结构 | 三仓当前 main 都是功能分支祖先；`git merge-tree --write-tree` 均成功，无合并冲突。没有修改实际分支或工作树。后续 main 变化须重查 |
| GitHub 生产准入 | 两仓 production 环境没有保护规则或部署分支限制；main `protected=false`，适用 rules 为空。不能假设合并后会停下来等人工批准 |
| GitHub 在途生产任务 | 查询时两仓 production-deploy 的 queued/in_progress/waiting/requested/pending 均为 0；这是当时快照，不能代表服务器代理内部没有队列 |

前后端及服务器的代码证据、混配验证和逐步操作依据见 [服务端首次发布审核](../../../NPClassworksKV/docs/NPEP-N1-RELEASE-REVIEW.md)。本轮不修改已通过检查的运行实现，不把文档审查声称为真实服务器验收。

## 河豚豚的现场回报与 HTTPS 核查（2026-09-21）

| 项目 | 已确认信息与边界 |
| --- | --- |
| 前端现场仓库 | `/NPClassworks`，HEAD `19756f654f94006084d1d954b8be4171db7c9a18`，工作树干净 |
| 后端现场仓库 | `/NPClassworksKV`，HEAD `f731f3227a7c59585aff940f78354585d3b016b7`，工作树干净 |
| Compose | 项目 `npclassworks`；backend/frontend 使用各自 `:current` 镜像，均 Up 22 hours (healthy)；postgres 使用 `postgres:17-alpine`，Up 4 weeks (healthy)。为操作者提供时的快照，尚未据此核实镜像对应提交 |
| 部署模式 | 河豚豚明确提供 `DEPLOY_MODE=shared`；沿用该模式，现场存在本项目 PostgreSQL 容器 |
| 部署代理入口 | 本机监听端口 17020，由 OpenResty 代理至 `https://deploy.newfires.top`；不用默认 19090，不开放本机端口 |
| 队列只读检查 | Codex GET `https://deploy.newfires.top/healthz`，TLS 校验开启；响应 Date `2026-09-21 12:24:41 GMT`（北京时间 20:24:41），HTTP 200，`{"ok":true,"busy":false,"queued":0}`。仅代表当时代理空闲，正式发布前须复查 |

用户随后补充确认：`NPEP_ENABLED` 未设置；作业、通知、学校管理等原有业务截至 2026-09-21 18:30（北京时间）正常。这两项不再要求河豚豚重复提供。业务状态来自用户反馈，维护前后仍需复查，不将此记录视为新版上线验收。

已收到的版本与此前旧版基线一致。Codex 此次未推送、连接 SSH、发起升级或更改服务器配置；河豚豚已执行备份、数据库隔离恢复并完成异地备份。env 位于 `/NPClassworksKV/deploy/.env.production`，维护时间灵活。部署代理为 PM2 `np-deploy-agent`（ID 4）；正式窗口先控制 GitHub 发布任务、排空代理队列，再使用同一 PM2 用户定向暂停应用，验收及处理旧请求后恢复。不使用 stop all/delete/save，不提前停止在线代理。容器 healthy 与代理 ok 不能代替业务验收。

NPClassworks/KV 图标变更现已纳入上方最终候选并推送功能分支；此前托管 CI 记录不覆盖这些变更，以新增最终组合检查为准。

## 为什么需要受控发布窗口

1. Actions 的 commit 仅作为部署请求元数据。`server.js` 调用固定脚本，后者在执行时读取两个 `origin/main`；它没有使用 CI 中记录的两个不可变 SHA。因此执行前 main 再变化，部署组合也可能变化。
2. 当前兼容性声明均为 epoch 1，没有 `requiresPeerCommit`。本地只读解析验证表明，新旧两端混合仍可通过此层检查。新 CI 可以阻止自己的不配套运行，但不能拦住旧任务或已经发给代理的请求。
3. 两个仓库各自触发一次升级会产生两次请求。第二次升级可能覆盖第一次上线前的回滚基线；不能把“相同版本再部署一次”视为完全没有影响。
4. KV 的 `docker-publish.yml` 还会在 main 推送时发布镜像。暂停 production-deploy 不等于暂停所有发布入口。是否使用这些镜像及需不需要同时暂缓推送，由服务器管理员在窗口前核对。

默认关闭 NPEP 不能代替上述控制：新镜像仍会执行数据库迁移。该迁移新增表、绑定版本列及旧业务表上的生命周期触发器；“关闭互联”不等于数据库完全没有变化。

## 推荐执行顺序

| 阶段 | 操作人与工作 | 进入下一阶段的条件 |
| --- | --- | --- |
| 1. 确认窗口 | 用户指定服务器操作者和维护时间；同意本次先发布关闭状态。由操作者确认生产部署入口可暂停或人工放行、实际代理队列为空，并冻结两端 main | 有明确操作者；自动发布和旧请求受控 |
| 2. 核对现场 | 操作者提供两仓实际 HEAD、Compose 项目名、`DEPLOY_MODE` 的实际值、开关布尔值、当前健康结论、备份及原镜像可恢复结论。我们核对脚本是否具备既有双 SHA 发布能力 | 沿用正确的项目、env 与卷；回退材料可用 |
| 3. 合入配套代码 | 在部署仍受控时先后端、再前端合入 main；NPEduTools 单独审核合入。由我们记录合并后完整 SHA，运行最终前后端组合的纯测试和所需检查 | 最终提交的检查通过；main 保持冻结 |
| 4. 一次精确部署 | 服务器操作者按审核后的现有 `upgrade.sh --backend-ref <后端完整SHA> --frontend-ref <前端完整SHA> --rollback-on-failure` 发布一次；实际工作目录和 env 先核对，不能直接照抄占位参数执行 | `deployed-release.json` 和实际镜像对应受测版本；原有业务正常；NPEP 仍关闭 |
| 5. 关闭状态验收 | 检查迁移、作业/通知/学校管理与原大屏流程；API 域名应返回 NPEP 的关闭信封而不是 404/HTML。核对升级前基线已独立保留 | 确认上线成功，处理旧任务后方可按约定恢复日常发布入口 |
| 6. 单独启用试点 | 另行确认后，操作者初始化关闭身份、核验 node 用户目录权限、同步宿主与容器开关，再显式激活。我们准备新设备包；现场人员在一台大屏完成双端配对 | [现场验收表](NPEP-N1-FIELD-ACCEPTANCE.md) 实测通过后才扩大范围 |

两仓的发布工作流包含部署步骤，不能以手动运行 production workflow 代替纯测试。已有 `contracts.yml` 支持指定前后端完整 SHA，后端 `quality.yml` 不部署。最终 main 合入结果若改变受测代码，须重新取得对应结果。当前没有修改 server.js 的必要；如将来希望取消人工版本冻结，再单独实现受测版本与部署版本的绑定。

现场现已确认 `DEPLOY_MODE=shared`，以河豚豚提供的配置值为准，不能根据网页/API 域名分开而改成 standalone。`shared` 也不能被误解为没有本项目 PostgreSQL 容器。

## 回退要求

2026-09-21 首次备份已有操作者回报：`/root/npclassworks-before-npep-fyhzmkZG`，`328M`，数据库 `NPClassworksKV`，dump 时间为北京时间 21:28:25，原始 dump 及八项存档文件校验均 OK。随后隔离恢复也已通过，用户表数量 31，临时容器与匿名卷已清理，记录目录 `restore-check-cng0O6eV`。结果见 [备份记录](NPEP-N1-FIRST-BACKUP.md) 和 [恢复记录](NPEP-N1-RESTORE-DRILL.md)。河豚豚已确认异地备份完成，目标设备校验未单独回报；应用镜像回退和完整离线恢复仍未因此验收。

- 升级前保留数据库备份校验、前后端镜像标识和原部署记录；只允许一次有意的升级请求，避免基线被覆盖。
- 现有自动回滚只覆盖启动后的 readiness 失败，不覆盖所有构建、启动错误。旧应用镜像配新 N1 数据库尚未做专门验收，不能承诺任意失败均可自动无损恢复。
- 如果已经启用过 NPEP，回退旧应用之前需要关闭互联并更换代际；保留独立 `npep-config` 卷，不随数据库一起还原，不使用 `down --volumes`。
- 数据库恢复会替换当前数据，需要另行确认可能损失的新写入。现有保护依赖运行中的可信 N1 容器；若后端已停止或已回退旧镜像，完整离线恢复流程尚未验收，应先处理该阻碍，不能忽略 helper 失败继续删除数据库。

## 大屏包与现场分工

本轮没有制作或发布新的 N1 便携包。现有打包脚本和程序版本仍默认 `InDev 20260920`，不能把新 N1 程序覆盖上传到旧版资产。试点前由我们明确新的构建标识与发布说明，从已验证的干净提交生成包，核对 manifest/哈希和双份 Recorder，再交给现场人员使用。继续保持不随包分发 FFmpeg 的现有方式。

现场人员只需在大屏正常 Windows 登录用户下使用新包，设置 API origin `https://api.newfires.top`，由学校管理员批准申请，并在大屏核对归属后确认。不要将开发电脑的 DPAPI 凭据复制过去。配对后的断网、休眠、重启和撤销需要现场人员配合；当前开发电脑不能替代这部分验收。

## 用户回来后需要做什么

操作者、路径、配置位置、现场版本、模式、业务状态和分阶段路线已确认。首次备份、隔离恢复、异地备份和最终托管检查均已完成。GitHub 三个发布入口暂停，PM2 stopped，两仓 main 已快进到最终受测 SHA。河豚豚现在执行 [受控发布记录](NPEP-N1-CONTROLLED-RELEASE.md) 第二段固定版本升级命令，回报末尾结果、manifest、容器状态和日志目录；失败停止反馈，不重复升级。验收原有业务及 NPEP 关闭状态后，再安排恢复代理及原工作流状态。

不需要发送账号密码、SSH 私钥、部署令牌或整个 env 文件。其他版本核对、测试与文档工作继续由我们处理；试点大屏人员和启用时间可在关闭状态发布完成后再安排。
