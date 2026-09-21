# 首次部署未就绪与自动回退记录

日期：2026-09-21。状态：新版构建成功但后端未通过就绪检查，已自动回退旧应用；保留失败镜像已确认源码权限导致 node 用户启动失败，正在准备修复和回归验证，不重复原升级命令、不恢复数据库。

## 已核实的事实

### 根因已确认：升级命令的 umask 范围错误

操作者在保留的失败镜像 `sha256:c0e7aac95c947ef8badc386e5e4778a52cb7d46555799b79fa6c58e01fa21b8d` 中确认：`/app`、`/app/scripts`、`/app/prisma` 均 root:root 755，但 `/app/package.json` 和 `/app/scripts/npep-config.js` 为 root:root 600。以镜像原定 node 用户执行配置检查，抛出 `EACCES: permission denied, open '/app/scripts/npep-config.js'`，退出码 1，Node.js v22.23.2。

Codex 交付的升级外层命令将 `umask 077` 用于整个流程，导致 root 检出受限源码；Docker 的源码复制保留权限。root 构建可读，运行时 node 无法读取首个启动脚本，因此迁移前即失败。这不是操作者输错或 NPEP 未激活造成的。合作任务另用无网只读容器和临时 tmpfs 复现 600/700 权限样本对 uid 1000 的启动阻断，恢复样本为 644/755 后成功；该实验不代替上述现场证据。

修复范围：私有备份/日志继续私有，源码构建权限单独处理；加强后端镜像对受限检出目录的兼容；排除构建上下文中的生产备份/runtime/秘密文件；补充回退前诊断保存与故障回归。源码修复由服务端合作任务处理，尚未推送或再次部署。不能只改 umask 后立即重跑：已有文件权限不会自动恢复。

下方“待诊断”段落为取证过程历史，当前结论以本节为准。

- 操作者日志目录：`/root/npclassworks-release-8uExEvBf`。
- 升级前新备份：`/NPClassworksKV/deploy/backups/npclassworks_NPClassworksKV_20260921T145406Z_pre-upgrade.dump`。这份比此前 21:28 存档更新，应保留，不覆盖。
- 目标后端 `e660876c8a22004e14a053a70985d5b350e42d0d`、前端 `37a3f1b01585fd8cfc751c921668a84d5c41264f` 的环境检查、镜像构建和前端 PWA 校验成功。
- 创建了 `npclassworks_npep-config` 卷；启动新前后端后，升级脚本的后端 readiness 等待失败。
- 日志显示自动应用回退执行完成，两仓 checkout 回到后端 `f731f3227a7c59585aff940f78354585d3b016b7`、前端 `19756f654f94006084d1d954b8be4171db7c9a18`。回退代码在旧后端 readiness 通过后才打印“回滚完成”。该脚本还 force-recreate 了 PostgreSQL 容器，但没有执行删库/数据库还原；不能据此判断新迁移完全没发生。
- Codex 于北京时间 23:00:13–14 只读访问 `https://api.newfires.top/ready` 返回 HTTP 200、`status=success`，`https://newfires.top/` HEAD 返回 HTTP 200。说明查询时业务入口可达，不冒充作业/通知/登录等完整业务验收。
- GitHub main 仍为新受测提交；现场应用已回退。三条 GitHub 发布入口与 PM2 继续保持暂停，不因旧版已恢复而重新开放自动部署。

## 尚不能下结论的部分

构建的 Sass 弃用、下载速度和浏览器数据库更新提示不是日志中的失败点。现有升级脚本没有在删除失败容器前保存该容器的启动/健康日志，因此仅凭所贴输出不能区分配置检查、迁移、应用启动、数据库连接或就绪等待时间问题；读取当前旧 backend 日志也不能当作新 backend 的错误日志。

先只读查询迁移历史及失败记录，确认当前容器状态，并以 node 用户只读检查新配置卷元数据。没有证据前不延长等待掩盖失败、不修改权限、不执行 migrate deploy/resolve、不重新升级或还原数据库。

## 交给河豚豚的只读诊断

下面不运行应用入口、不执行迁移，只用现用镜像启动一个无网络、只读的临时 Node 进程查看卷权限，结束后自动清理该临时容器。不要清理 dangling 镜像或 npep-config 卷，它们可能包含排查需要的证据。

```bash
(
  set -eu
  git -C /NPClassworksKV rev-parse HEAD
  git -C /NPClassworks rev-parse HEAD
  docker inspect --format '{{.Name}} status={{.State.Status}} restarts={{.RestartCount}} health={{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} image={{.Image}}' \
    npclassworks-backend-1 npclassworks-frontend-1 npclassworks-postgres-1

  docker exec -i npclassworks-postgres-1 sh -c \
    'exec psql -X -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -P pager=off' <<'SQL'
BEGIN READ ONLY;
SELECT migration_name, started_at, finished_at, rolled_back_at, applied_steps_count
FROM "_prisma_migrations" ORDER BY started_at DESC LIMIT 5;
SELECT migration_name, left(coalesce(logs, ''), 2000) AS error_excerpt
FROM "_prisma_migrations"
WHERE finished_at IS NULL AND rolled_back_at IS NULL
ORDER BY started_at DESC LIMIT 3;
SELECT to_regclass('public."NpepDeployment"') IS NOT NULL AS npep_table_exists;
COMMIT;
SQL

  docker volume inspect --format '{{.Name}}' npclassworks_npep-config
  docker run --rm --pull=never --network none --read-only --user node \
    --mount type=volume,source=npclassworks_npep-config,target=/npep,readonly \
    --entrypoint node npclassworks-backend:current -e '
      const fs = require("node:fs");
      try {
        const s = fs.statSync("/npep");
        const files = fs.readdirSync("/npep");
        console.log(JSON.stringify({uid:s.uid,gid:s.gid,mode:(s.mode & 511).toString(8),deploymentFileExists:files.includes("deployment.json")}));
      } catch(e) { console.error("npep-volume:", e.code); process.exitCode=1; }
    '
)
```

根因、修正方案及再次部署条件在取得输出后补充。当前未修改应用代码或重新启用发布入口。

## 第二轮：数据库与配置卷结果已返回

河豚豚回报：两仓 HEAD 均已回到上述旧版；backend、frontend、postgres 三容器 running/healthy，restart count 均为 0。迁移历史最新为 `20260909000000_account_notice_certification`，无 unfinished 且未 rolled back 的迁移；`NpepDeployment` 不存在。配置卷 uid/gid 为 1000/1000、权限 700、`deployment.json` 不存在。

这些结果没有显示 N1 迁移已应用或留下失败记录，不等于数据库所有内容均未变化；应用仍可能有正常业务写入。当前无证据需要还原数据库，配置卷权限也符合 Dockerfile 的预期。

发现待证实的具体原因：Codex 给出的升级外层命令将 `umask 077` 覆盖到 git checkout 和构建。新检出的源码文件/目录可能成为 root 所有的 600/700，后端 `COPY . .` 将其带入镜像，构建步骤使用 root 可通过，运行时 `USER node` 却无法读取入口或依赖源码。升级脚本/lib 没有自行调整 umask。这是操作命令作用范围的问题，不能归因于操作者或未开启 NPEP；尚需失败镜像实际权限与 node 启动错误确认。

下一步使用 [镜像只读诊断脚本](NPEP-N1-IMAGE-READONLY-DIAGNOSTIC.sh)：仅查找构建日志对应的保留镜像，禁网络、只读根文件系统、无生产卷/环境/数据库连接。先查看固定源码路径元数据，再用 node 用户单独运行配置检查，保留错误和退出码；不启动完整应用、不运行 Prisma、不尝试修权限。镜像不存在时停止，不回落到 current 旧镜像、不拉取或重建。自动发布入口继续暂停。

协作方只读复核：目标启动顺序为配置检查 → Prisma migrate deploy → 应用进程。false 且 deployment.json 缺失允许启动，未激活 NPEP 本身不会阻止 /ready，不能凭缺配置判定原因。新增迁移名为 `20260920120000_npep_n1`。若数据库已迁移但无原启动日志，可后续使用保留的新镜像和备份恢复的隔离库复现完整启动，不能直接重试生产碰运气；镜像构建 config digest 在日志中为 `sha256:e3227d12831303a2c2d9a433a776623ecd77973197b749a5818af4b0109a5404`，实际可寻址镜像标识需 Docker inspect 确认。
