# NPEP N1 受控发布操作记录

> 当前结果：河豚豚已成功执行修复版重试，后端 401182f / 前端 37a3f1b，三个容器 healthy，公网关闭状态验证通过。记录 `/root/npclassworks-permission-fix-s5Wv58lU`；等待登录、作业、通知、学校管理业务验收，暂不恢复部署入口。以下“尚未执行”及原升级命令为历史，均不得重跑。详见 [现场结果](NPEP-N1-PERMISSION-RECOVERY.md)。

> 最新状态：首次执行已回退健康旧版，根因确认为下方历史升级命令的全局 umask 077 使源码权限为 root:root 600，node 启动 EACCES。下方命令已作废，保留用于事件溯源。修复 401182f 已通过本地与托管验证，并于 23:39 快进后端 main；新的 [重试脚本](NPEP-N1-PERMISSION-RETRY.sh) 和 [恢复说明](NPEP-N1-PERMISSION-RECOVERY.md) 已就绪，尚未在服务器执行。PM2 与 GitHub 发布入口继续暂停。

## 当前阶段：PM2 已停止、main 已快进，等待服务器执行升级

河豚豚已回报 `np-deploy-agent`（ID 4）为 stopped，停止前代理空闲。Codex 于北京时间 22:48–22:49 再次核对三条发布工作流均禁用、三个测试运行均 success、两条功能分支无漂移及 main 祖先关系，然后依次将后端、前端 main 非强制快进到受测提交：

- 后端 `e660876c8a22004e14a053a70985d5b350e42d0d`
- 前端 `37a3f1b01585fd8cfc751c921668a84d5c41264f`

API `force=false`，未生成新的合并提交、未重写历史。见 [main 前置核查](NPEP-N1-MAIN-PREFLIGHT.json)、[main 实际结果](NPEP-N1-MAIN-RESULTS.json) 和 [快进后发布入口复查](NPEP-N1-POST-MAIN-GATES.json)。三个发布入口仍为 disabled_manually，查询其最近运行未发现未完成任务；网站尚未由 Codex 发起升级。此时交付下方固定版本升级命令，等待河豚豚执行结果。

用户已明确授权开始处理发布控制。北京时间 2026-09-21 22:34–22:35，Codex 保存原状态后，逐一暂停并读回确认：

| 仓库 | 工作流 | ID | 原状态 → 当前状态 |
| --- | --- | --- | --- |
| NPClassworks | production-deploy.yml | 345174965 | active → disabled_manually |
| NPClassworksKV | production-deploy.yml | 345174964 | active → disabled_manually |
| NPClassworksKV | docker-publish.yml | 330817952 | active → disabled_manually |

暂停后复查两仓 queued、in_progress、waiting、requested、pending 均无运行；22:35:13 部署代理 HTTPS 状态为 `ok=true,busy=false,queued=0`。未取消执行中的部署，因为没有发现执行中任务。纯测试工作流保持启用；手动 GitHub Pages 镜像发布不是本次生产路径，未运行或修改。

原始与修改后状态分别记录在：

- [控制前快照](NPEP-N1-RELEASE-CONTROL-SNAPSHOT.json)
- [逐项操作结果](NPEP-N1-RELEASE-CONTROL-CHANGES.json)
- [暂停后队列快照](NPEP-N1-RELEASE-CONTROL-QUEUE.json)

两仓 main 现已快进，服务器仍待手动升级。GitHub 禁用工作流不是禁止其他管理员主动推送或重新启用入口的权限锁；本次维护期间需保持两仓 main 和发布设置不被其他人改变。

## 已完成：河豚豚暂停部署代理（无需重跑）

在原先执行 `pm2 list` 的同一用户会话（当前为 root）中运行：

```bash
(
  set -Eeuo pipefail
  curl --proto '=https' --fail --silent --show-error --max-time 15 \
    https://deploy.newfires.top/healthz |
    node -e 'const s=JSON.parse(require("node:fs").readFileSync(0,"utf8")); if(s.ok!==true || s.busy!==false || s.queued!==0){console.error("部署代理仍在忙或状态异常，未停止");process.exit(1)} console.log("部署代理空闲，准备暂停")'
  pm2 stop np-deploy-agent
  pm2 list
)
```

发回 `np-deploy-agent` 那一行，预期 `stopped`。这只停止部署代理，不停止 Docker 中的网站、API 或数据库。若状态查询失败或显示在忙，脚本在 stop 前退出，反馈错误，不直接结束忙碌中的代理。暂停后部署域名可能返回 502/503，业务域名不因此停机。

不执行 `pm2 stop all`、`pm2 delete`、`pm2 save`、`git pull` 或 Docker 清理。GitHub 侧原状态已经保存，之后由 Codex 按原状态恢复；不要求河豚豚另外寻找 GitHub 开关。

## 发布顺序（第 1–3 项已完成，第 4 项待现场执行）

1. 收到 PM2 stopped 后，Codex 再读三条 workflow 状态和 main，确认未漂移。
2. 两仓 main 与候选现已核实为祖先关系，可以在控制入口下将 main 按后端、前端顺序快进到已测试的完整提交，不重写历史、不生成不同内容的合并版本。发生分支漂移则停止重核，不 force push。
3. 记录 main 实际结果。前端必须为 `37a3f1b01585fd8cfc751c921668a84d5c41264f`，后端必须为 `e660876c8a22004e14a053a70985d5b350e42d0d`；若最终提交发生变化，不沿用旧 CI 结论。此步骤可能触发纯质量检查，但三个发布入口保持禁用。
4. 河豚豚从服务器旧版工作树执行现有 `deploy/upgrade.sh`，传入两个完整目标 SHA。**不要先 git pull/checkout 到新版**：脚本必须先记录真实旧版引用、升级前备份和旧镜像，再切换工作树。旧版脚本已具备双 SHA 参数；需要明确 `ENV_FILE=/NPClassworksKV/deploy/.env.production`、shared、NPEP=false，并核对运行镜像与 `:current` 标签一致。精确升级命令在回退步骤核对完成且 main 已快进后单独交付，此处不提前给执行许可。
5. 核对 `deployed-release.json`、容器健康、宿主与容器的 NPEP 关闭状态；经 API origin 请求 `/api/v2/npep/info` 时带 `X-NPEP-Version: 0.1`，应收到 HTTP 503 与 `TEMPORARILY_UNAVAILABLE` JSON，不能把缺协议头的 426 或反向代理 HTML 算作关闭状态通过。再由现场人员验证作业、通知、学校管理、登录和原大屏业务。
6. 完成验收和清理旧请求后，河豚豚使用同一 PM2 用户 `pm2 restart np-deploy-agent`，Codex 核对 HTTPS 空闲状态，再依保存状态恢复三条 GitHub 发布工作流。不重跑旧部署任务，不为补一次发布记录而重复升级。

## 回退边界与已有材料

### 固定版本升级命令（当前交付步骤）

两仓 main 已确认快进、PM2 已停止，已有存档与数据库恢复验证。现在从服务器当前旧工作树执行下段，不要提前 checkout 或 git pull；旧版 `upgrade.sh`、`lib.sh`、`rollback.sh`、`release-plan.js` 与目标提交逐文件对比无差异。首次 pre-upgrade 备份仍调用旧版 backup.sh，本次单次执行不应声称已使用新版唯一文件名修复。应用切换时可能有短暂不可用；数据库回退不在自动授权范围内，失败按下方分级路径处理。

```bash
(
  set -Eeuo pipefail
  umask 077
  stage=预检查
  trap 'rc=$?; printf "%s失败，先停止并回报输出，不要重复升级。\n" "$stage" >&2; exit "$rc"' ERR
  cd /NPClassworksKV
  export ENV_FILE=/NPClassworksKV/deploy/.env.production
  export GIT_TERMINAL_PROMPT=0
  source deploy/lib.sh
  load_production_env
  test "$DEPLOY_MODE" = shared
  test "${NPEP_ENABLED:-false}" = false
  test "$(git rev-parse HEAD)" = f731f3227a7c59585aff940f78354585d3b016b7
  test "$(git -C /NPClassworks rev-parse HEAD)" = 19756f654f94006084d1d954b8be4171db7c9a18
  require_clean_repository "$REPO_ROOT" 后端
  require_clean_repository "$FRONTEND_ROOT" 前端
  test "$(git ls-remote origin refs/heads/main | cut -f1)" = e660876c8a22004e14a053a70985d5b350e42d0d
  test "$(git -C /NPClassworks ls-remote origin refs/heads/main | cut -f1)" = 37a3f1b01585fd8cfc751c921668a84d5c41264f
  pm2 jlist | node -e 'const a=JSON.parse(require("node:fs").readFileSync(0,"utf8")).filter(x=>x.name==="np-deploy-agent");if(a.length!==1||a[0].pm2_env.status!=="stopped")process.exit(1)'
  for service in backend frontend; do
    test "$(docker inspect --format '{{.Image}}' "npclassworks-$service-1")" = "$(docker image inspect --format '{{.Id}}' "npclassworks-$service:current")"
  done
  test "$(docker inspect --format '{{.Image}}' npclassworks-postgres-1)" = "$(docker image inspect --format '{{.Id}}' postgres:17-alpine)"
  log_dir=$(mktemp -d /root/npclassworks-release-XXXXXXXX)
  printf '升级记录目录：%s\n' "$log_dir"
  stage=升级
  bash deploy/upgrade.sh \
    --backend-ref e660876c8a22004e14a053a70985d5b350e42d0d \
    --frontend-ref 37a3f1b01585fd8cfc751c921668a84d5c41264f \
    --rollback-on-failure 2>&1 | tee "$log_dir/upgrade.log"
  stage=升级后检查
  test "$(git rev-parse HEAD)" = e660876c8a22004e14a053a70985d5b350e42d0d
  test "$(git -C /NPClassworks rev-parse HEAD)" = 37a3f1b01585fd8cfc751c921668a84d5c41264f
  compose exec -T backend node -e 'if(process.env.NPEP_ENABLED!=="false")process.exit(1);fetch("http://127.0.0.1:3000/ready").then(r=>{if(!r.ok)process.exit(1);console.log("后端就绪；NPEP 保持关闭")}).catch(()=>process.exit(1))'
  cp "$RUNTIME_DIR/rollback-state.env" "$log_dir/rollback-state.env"
  cp "$RUNTIME_DIR/deployed-release.json" "$log_dir/deployed-release.json"
  cat "$RUNTIME_DIR/deployed-release.json"
  compose ps
  printf '\n升级命令完成。记录：%s\n先保留代理 stopped，等待业务验收。\n' "$log_dir"
)
```

先 `source` 实际 env 再核对关闭值，因为命令前缀赋值会被脚本随后加载的 env 覆盖。不要打印完整环境或 `pm2 jlist`；上述 JSON 只在管道内检查应用状态。目标代码不会在 false 下自动初始化身份；如果已有 deployment.json，仍校验其内容，异常时停止调查。

成功后单独读回两个 HEAD、runtime/deployed-release.json，并用同一 env/Compose 设置检查 backend 中的实际 `NPEP_ENABLED` 为 `false`；宿主检查不能替代容器有效值检查。完成业务验收前不恢复发布入口。

已有私有存档 `/root/npclassworks-before-npep-fyhzmkZG`（328M）及异地副本；数据库隔离恢复 31 张用户表通过。正式升级仍需脚本创建最新 pre-upgrade dump 和独立旧镜像标签，保留本次 rollback-state，不再次升级覆盖它。

现有脚本只在启动后的 readiness 失败时处理自动应用回退，并非所有错误均自动恢复。构建失败可能已切换工作树或改变 `:current` 标签；应保留输出，由回滚状态判断实际容器和镜像，不能直接再跑升级。应用回退不还原数据库，新迁移后的旧应用兼容性与整套离线恢复尚未验收。

新版数据库恢复脚本依赖正在运行的可信 N1 helper 做配置核对，后端起不来或已回退旧镜像时不能直接照抄 `--restore-database --yes`，更不能跳过 helper 后删库。数据库还原会丢失快照以后的写入，必须先针对实际故障确认恢复点与数据影响。本轮不启用 NPEP，不初始化或激活 deployment identity，不删除 npep-config 卷。正式升级指令将连同可执行的失败处理方案一起交付。

### 失败时逐级处理（不自动执行数据库回滚）

1. 停止后续发布操作，保留命令输出。先确认此次升级是否已生成新的 runtime/rollback-state.env，以及其中旧 SHA、BACKUP_FILE、旧镜像标签与本次操作一致；不能把历史 state 当本次 state。将本次状态和 deployed-release/升级计划另存到已建立的私有存档目录，防止被重复升级覆盖。
2. 已有正确的本次回滚状态时，应用回退命令为在 `/NPClassworksKV` 执行 `ENV_FILE=/NPClassworksKV/deploy/.env.production bash deploy/rollback.sh`。它不删除数据库。可能已经由 upgrade 自动调用；先看输出和实际状态，不盲目再执行。
3. 本次从未启用 N1、从未初始化/激活/颁发授权且始终 false 的前提下，若应用回退后仍需旧库：先确认实际后端/前端 HEAD 已分别回到 `f731f322...` / `19756f65...`，current 镜像是保存的旧镜像，再讨论从**旧 checkout** 执行旧 restore.sh。它无运行中 N1 helper 的依赖，但会先安全备份当前数据库，再停止旧 backend 并还原，因此不是在新版 restore 里跳过保护。恢复路径必须位于实际 BACKUP_DIR，选用经校验的升级前 dump，不能默认使用较早的 21:28 存档；完整路径从本次已核验 state 确定。
4. 数据库还原有删除当前数据的效果，实际故障时须先确认快照后新写入的影响，届时再给含真实 dump 路径的命令；不预先授权自动 `--restore-database --yes` 或 `--skip-safety-backup`。
5. 上述旧版恢复路径只适用于此次有证据的 never-enabled 首发；不是已启用 NPEP 后的通用恢复方法。无法确认前提、身份文件异常、镜像标签不符或数据库本身不可用时停止，保留状态并针对故障处理。

此处为源码复核的恢复路径；只完成了数据库隔离还原，尚未宣称整套生产应用回退和旧应用兼容新迁移的现场演练通过。
