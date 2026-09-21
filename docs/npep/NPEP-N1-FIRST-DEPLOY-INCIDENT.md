# 首次部署未就绪与自动回退记录

日期：2026-09-21。状态：新版构建成功但后端未通过就绪检查，已按现场日志自动回退旧应用；根因待只读诊断，不重复升级、不恢复数据库。

## 已核实的事实

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

协作方只读复核：目标启动顺序为配置检查 → Prisma migrate deploy → 应用进程。false 且 deployment.json 缺失允许启动，未激活 NPEP 本身不会阻止 /ready，不能凭缺配置判定原因。新增迁移名为 `20260920120000_npep_n1`。若数据库已迁移但无原启动日志，可后续使用保留的新镜像和备份恢复的隔离库复现完整启动，不能直接重试生产碰运气；镜像构建 config digest 在日志中为 `sha256:e3227d12831303a2c2d9a433a776623ecd77973197b749a5818af4b0109a5404`，实际可寻址镜像标识需 Docker inspect 确认。
