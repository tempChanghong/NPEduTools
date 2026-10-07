# Pre 20261001：合并与检查记录

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

本次交付为三仓源码预览版，三仓已于 2026-10-01 至 2026-10-02 合并 main。暮至长虹批准在真实班级大屏与学校排程现场验收未完成时先合并；未测项目保留为待验收。没有构建或上传新版桌面 ZIP，没有将源码合并或 CI 通过当作生产验收通过。

## 主分支

| 仓库 | PR | 候选提交 | main 合并提交 | 状态 |
| --- | --- | --- | --- | --- |
| NPEduTools | [#3](https://github.com/tempChanghong/NPEduTools/pull/3) | `417457c40419c594d917ae28aed114b7263e702c` | `7d91866160e65732b5a47c49341a809b46a2fdfd` | 已合并 |
| NPClassworksKV | [#1](https://github.com/tempChanghong/NPClassworksKV/pull/1) | `2b5de5bbf0e99b850e37550aa72efffd004ba0f5` | `83fde6db8553e259d41192672ca6717ad11593bb` | 已合并 |
| NPClassworks | [#1](https://github.com/tempChanghong/NPClassworks/pull/1) | `1eb7f37f704e06ddd6373d5a51e7b16a6d595b2c` | `6ebfd72acfd853c902b34189e2b3cfcd1d0384d9` | 已合并 |

仓库 main 会触发既有流水线。网页和 KV 的包版本仍是 `1.1.0`，核对版本组合应使用提交，而不是仅看包版本。桌面标识为 `Pre 20261001`／`0.1.0-pre.20261001`。

## 已执行验证

- N4 最终隔离验收：七阶段通过，778 项代码测试、28 项临时原生 PostgreSQL／HTTP／Host 验收及两套隔离浏览器流程。结果位于本机 `.artifacts/npep-n4/2026-10-01T15-25-22-978Z-9e096a45/result.json`，详见 [统一验收](../npep/N4-UNIFIED-ACCEPTANCE-20261001.md)。这是提交前候选工作区的测试；本记录不将它描述为所有最终合并提交的重新运行。
- 网页全量单元测试 654 项、ESLint、隔离生产构建／静态 PWA 校验通过；后端发布配置相关测试 21 项通过。详见 [网页生产构建检查](../npep/N4-PRODUCTION-BUILD-CHECK-20261001.md)。
- N3 方案与控制的契约、网页 13 项、后端 29 项、Host 154 项、传输 127 项检查通过。首次隔离浏览器阶段因 Windows 端口占用失败；修正测试服务器的临时端口分配后，单独重跑实际浏览器阶段通过。首次聚合运行仍保留失败记录。
- 后端全量单元测试 355 项通过，17 项数据库条件测试跳过；不能将跳过项计为通过。GitHub 的后端 PostgreSQL 集成作业另行通过。
- ExamAware 桥接插件 25 项测试及 TypeScript 构建通过；没有构建桌面 App 发行包。
- NPEduTools 候选 [Windows PR 检查](https://github.com/tempChanghong/NPEduTools/actions/runs/36885153741)通过；KV 候选 [质量与 PostgreSQL 集成检查](https://github.com/tempChanghong/NPClassworksKV/actions/runs/36885147208)通过。
- NPEduTools 合并提交的 [Windows 检查](https://github.com/tempChanghong/NPEduTools/actions/runs/36886669696)通过。KV main 的 [部署前检查](https://github.com/tempChanghong/NPClassworksKV/actions/runs/36885834038)中，单元／数据库验证、真实 PWA 全栈与 N1 配对验收通过；部署作业已发出升级请求，完成状态另行核对。

## 网页检查修正

Claude 的旧审查任务在真正审查代码前失败：仓库没有安装 Claude Code GitHub App，工作流也未取得 API 密钥。自动审查改为仓库变量 `CLAUDE_REVIEW_ENABLED=true` 时显式开启；默认跳过，不忽略启用后的服务错误，也不影响构建、单元测试、浏览器或契约检查。最新 [Claude 运行](https://github.com/tempChanghong/NPClassworks/actions/runs/36886602311)为跳过；历史失败保留。

全栈通知测试原先查找旧名称“大屏弹窗提示”，现已与真实界面的“网页大屏与 NPEduTools 弹窗提示”保持一致；通知等级、开关、投递、数据库回执断言保留。随后实际全栈运行中，38 项里 37 项通过、1 项按独立 NPEP 门槛暂跳过；没有将这项跳过当作 N1 配对通过。

独立 N1 配对门槛找到了另一个过期确认框名称。将管理页文案对齐“配对即授权当前学校互联功能”，同时更新模拟后端与真实 PostgreSQL 两处浏览器定位；批准、设备确认、状态上报和撤销后拒绝访问断言不删减。

完整网页浏览器检查先有 151 项通过、1 项失败：旧配对夹具把新版管理页所有 NPEP 请求都假设为 `0.1`，而排程读取实际使用 `0.7`。现明确校验排程读取为 `0.7` 并返回空规则目录，其余配对请求仍严格要求 `0.1`。本机两项相关浏览器流程重跑通过。最终 [网页 CI](https://github.com/tempChanghong/NPClassworks/actions/runs/36888424584)全部通过：单元／代码检查、152 项浏览器检查、真实全栈 37 项及独立 N1 配对 1 项。通用全栈中的 1 项 N1 跳过由后续独立门槛实际运行覆盖。最新 [PWA 构建](https://github.com/tempChanghong/NPClassworks/actions/runs/36888424138)也通过。

## 部署与现场边界

部署流水线完成、服务健康、数据库迁移和真实使用是不同的证据。合并收尾时生产检查只确认网站可访问、KV `/ready` 返回 200；这些不能证明新功能已上线。KV [生产流水线](https://github.com/tempChanghong/NPClassworksKV/actions/runs/36885834038)已通过部署前检查、发出升级请求，记录时仍在执行；网页 main 合并已触发配套检查和部署流程。没有确认服务器升级及迁移完成，不重跑首次激活，也没有直接登录或改动服务器配置。

真实班级大屏、学校时间／排程、长期运行及补充故障测试仍待验收。已知 ExamAware 编辑器退出协商问题按用户决定暂缓修复。噪音只上传统计，不保存或上传原音频；dBFS 不是校准声压级。完整范围、升级和文件说明见 [Pre 发版说明](Pre-20261001.md)。
