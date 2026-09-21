# NPEP N1 GitHub 托管检查记录

日期：2026-09-21。承接 [Docker 与 CI 接入记录](NPEP-N1-DEPLOYMENT-CI-REPORT.md)，本轮推送三个功能分支，并运行不含部署步骤的 GitHub Actions。没有合并 main、修改线上配置或进行学校配对。

## 受测提交与运行

| 仓库 | 功能分支 | 受测完整提交 | 托管运行 |
| --- | --- | --- | --- |
| NPEduTools | `codex/npep-n1-ci` | `96c2dad424d74c6becf7a0d49e3721df20e93d15` | [Windows N1 检查](https://github.com/tempChanghong/NPEduTools/actions/runs/35561890557) |
| NPClassworks | `codex/npep-n1-admin-ui` | `e354bbd172190a8a201120d1da8356abfc9e7a33` | [固定前后端组合检查](https://github.com/tempChanghong/NPClassworks/actions/runs/35561798752) |
| NPClassworksKV | `codex/npep-n1-server` | `88134b175e7f95055aacdb525b8b10dee1e6bc35` | [Quality 与 PostgreSQL 集成](https://github.com/tempChanghong/NPClassworksKV/actions/runs/35561797164) |

网页 `contracts.yml` 通过手动触发，`frontend_ref` 与 `backend_ref` 分别固定为表中完整 SHA。没有采用仍缺少 N1 的另一端 main 作为测试对象。后端仅触发 `quality.yml`；设备端由功能分支推送触发。未调用生产部署 workflow 或部署代理。

## 实际结果

表中三个运行均已完成，结论均为 **success**。已读取各 job 日志并下载 Windows 与网页产物核对，而非仅依据绿色状态。

| 检查 | GitHub runner 实际结果 |
| --- | --- |
| Windows 结构示例 | 41/41 |
| Windows Release 构建 | App、NpepProbe、Npep.Acceptance 三个入口通过，各 0 警告、0 错误 |
| Windows NPEP 专项 | 86/86，零跳过；TRX 逐项门槛通过 |
| Windows 原有回归 | 352/352，零跳过；TRX 逐项门槛通过 |
| 前后端契约 | 3/3，零跳过 |
| 通用生产 PWA 全链路 | 37 通过、1 跳过；被跳过的是需要显式启用隔离开关的 N1 用例，随后单独强制运行 |
| N1 真实浏览器与 PostgreSQL | 1/1；`skipped=0`、`unexpected=0`、`flaky=0`、`retry=0`，涵盖管理员批准配对、设备确认和撤销 |
| 全链路数据库会话前置检查 | 两次运行各 1/1，零跳过 |
| KV 默认单元命令 | 187 通过、17 个数据库用例在此命令中跳过 |
| KV 独立 PostgreSQL 集成 | 113/113，零跳过 |
| KV 生产 Docker 门槛 | 实际 Compose、镜像用户、持久化目录、原子写入和拒绝错误配置检查通过 |

默认命令中的跳过不计为数据库或 N1 验收通过。本轮没有另行运行前端 `tests.yml` 的单元/lint 工作流，因此先前本机的 513 项单元结果不冒充本轮托管结果。

Windows 产物仅含两份 TRX 与 `run.json`：提交与上表一致，`dirty=false`、`completed=true`，PowerShell 7.6.6；SDK 安装日志为 10.0.400。网页两份版本记录均对应上表前后端 SHA，且 `dirty=false`；N1 JSON 报告确认实际执行成功。Windows 产物保存 14 天，网页产物保存 7 天，过期后应查对应运行日志或重新取得报告。

| 托管产物 | 下载 ZIP 的 SHA-256 |
| --- | --- |
| [Windows 结果](https://github.com/tempChanghong/NPEduTools/actions/runs/35561890557/artifacts/10623025575) | `729711c7c514e7f4e3176a9e877f100c14a26bb6171d0f9eba7efb48d09b857f` |
| [网页全链路结果](https://github.com/tempChanghong/NPClassworks/actions/runs/35561798752/artifacts/10622841010) | `986a1d6854b090cf5835ed14a7a6477bc0150aa6086f93dbc8d8b150cc8fa858` |

## 托管环境发现并修复的问题

首次设备运行 [35561755497](https://github.com/tempChanghong/NPEduTools/actions/runs/35561755497) 在 checkout 收尾阶段失败，测试尚未开始。仓库包含三个参考文档 gitlink，却没有 `.gitmodules` 映射；`persist-credentials: false` 的凭据清理执行 `git submodule foreach` 时出现 `No url found for submodule path 'docs/ExamAware-docs' in .gitmodules`。

提交 `96c2dad` 根据本机已有参考仓库的实际 remote 补齐 ExamAware-docs、PowerPoint-Touch-Assist、classisland-docs-next 三个映射，没有改变 gitlink 指向的提交。相同本地 foreach 命令修复后正常结束。重跑已完成检出、全部测试及产物上传。

同时补上结果上传的 `include-hidden-files: true`，使 `.artifacts` 下明确列出的 TRX 与 `run.json` 可以保留。上传范围仍仅为这两类测试结果，不包含凭据目录或整个 `.artifacts`。

## 发布边界与下一步

托管检查结束后，通过 `git ls-remote` 复核 main 与本轮操作前一致：

| 仓库 | 未改变的 main SHA |
| --- | --- |
| NPEduTools | `2d25809eabf26d360337b1517dac7c0c735b21f6` |
| NPClassworks | `19756f654f94006084d1d954b8be4171db7c9a18` |
| NPClassworksKV | `f731f3227a7c59585aff940f78354585d3b016b7` |

main 的生产部署行为保持原样；本轮仅完成分支测试，不代表线上已经支持 NPEP。后续发布仍需审核三端提交和前后端成对发布方式。当前部署代理会解析另一端 main，本次固定 SHA 的 CI 不能证明将来的代理自动使用同一个组合。本文是受测代码之后的文档记录，受测提交以表中不可变 SHA 为准。

发布后保持 NPEP 外部配置关闭，核对迁移、卷和恢复入口，再单独进行初始化、启用与真实大屏试点。现场按 [验收表](NPEP-N1-FIELD-ACCEPTANCE.md) 填写；开发电脑和托管 runner 均不替代真实大屏验收。N1 仍只开放 `device.status`，未加入通知、远程考试模式切换或录制控制。
