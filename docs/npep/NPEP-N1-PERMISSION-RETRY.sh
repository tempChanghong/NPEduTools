#!/usr/bin/env bash
# Fixed-candidate retry; use only after its CI passes and controlled main promotion.
(
  set -Eeuo pipefail
  umask 077
  stage=预检查
  trap 'rc=$?; printf "%s失败，流程已停止。请回报输出，不要重复执行。\n" "$stage" >&2; exit "$rc"' ERR
  target_backend=401182fa97866820828df37a9a1b52884056964b
  target_frontend=37a3f1b01585fd8cfc751c921668a84d5c41264f
  previous_backend=f731f3227a7c59585aff940f78354585d3b016b7
  previous_frontend=19756f654f94006084d1d954b8be4171db7c9a18
  cd /NPClassworksKV
  export ENV_FILE=/NPClassworksKV/deploy/.env.production
  export GIT_TERMINAL_PROMPT=0
  source deploy/lib.sh
  load_production_env
  test "$DEPLOY_MODE" = shared
  test "${NPEP_ENABLED:-false}" = false
  test "$(git rev-parse HEAD)" = "$previous_backend"
  test "$(git -C /NPClassworks rev-parse HEAD)" = "$previous_frontend"
  require_clean_repository "$REPO_ROOT" 后端
  require_clean_repository "$FRONTEND_ROOT" 前端
  test "$(git ls-remote origin refs/heads/main | cut -f1)" = "$target_backend"
  test "$(git -C /NPClassworks ls-remote origin refs/heads/main | cut -f1)" = "$target_frontend"
  pm2 jlist | node -e 'const a=JSON.parse(require("node:fs").readFileSync(0,"utf8")).filter(x=>x.name==="np-deploy-agent");if(a.length!==1||a[0].pm2_env.status!=="stopped")process.exit(1)'
  for service in backend frontend; do
    test "$(docker inspect --format '{{.State.Health.Status}}' "npclassworks-$service-1")" = healthy
    test "$(docker inspect --format '{{.Image}}' "npclassworks-$service-1")" = "$(docker image inspect --format '{{.Id}}' "npclassworks-$service:current")"
  done
  test "$(docker inspect --format '{{.State.Health.Status}}' npclassworks-postgres-1)" = healthy
  test "$(docker inspect --format '{{.Image}}' npclassworks-postgres-1)" = "$(docker image inspect --format '{{.Id}}' postgres:17-alpine)"
  previous_backend_image="$(docker inspect --format '{{.Image}}' npclassworks-backend-1)"
  previous_frontend_image="$(docker inspect --format '{{.Image}}' npclassworks-frontend-1)"
  log_dir="$(mktemp -d /root/npclassworks-permission-fix-XXXXXXXX)"
  printf '本轮私有记录：%s\n' "$log_dir"
  if [ -f "$RUNTIME_DIR/rollback-state.env" ]; then
    cp "$RUNTIME_DIR/rollback-state.env" "$log_dir/previous-rollback-state.env"
  fi
  if [ -f "$RUNTIME_DIR/deployed-release.json" ]; then
    cp "$RUNTIME_DIR/deployed-release.json" "$log_dir/previous-deployed-release.json"
  fi

  # Old driver records old refs. Do not pre-checkout the target.
  # Source checkout/build uses 022; private log directory remains 0700.
  # Deliberately omit --rollback-on-failure so evidence can be saved first.
  stage=升级
  if (umask 022; bash deploy/upgrade.sh --backend-ref "$target_backend" --frontend-ref "$target_frontend") 2>&1 | tee "$log_dir/upgrade.log"; then
    stage='升级后核验（尚未自动回退）'
    test "$(git rev-parse HEAD)" = "$target_backend"
    test "$(git -C /NPClassworks rev-parse HEAD)" = "$target_frontend"
    compose exec -T backend node -e 'if(process.env.NPEP_ENABLED!=="false")process.exit(1);fetch("http://127.0.0.1:3000/ready").then(r=>{if(!r.ok)process.exit(1);console.log("后端就绪；NPEP 关闭")}).catch(()=>process.exit(1))'
    cp "$RUNTIME_DIR/rollback-state.env" "$log_dir/rollback-state.env"
    cp "$RUNTIME_DIR/deployed-release.json" "$log_dir/deployed-release.json"
    cat "$RUNTIME_DIR/deployed-release.json"
    compose ps
    printf '升级完成；代理继续保持停止，等待业务验收。记录：%s\n' "$log_dir"
  else
    stage=失败取证与应用回退
    printf '升级失败；先保留诊断，再验证本轮状态并回退应用。\n' >&2
    if [ -f "$DEPLOY_DIR/capture-failure.js" ]; then
      node "$DEPLOY_DIR/capture-failure.js" "$REPO_ROOT" "$ENV_FILE" "$COMPOSE_FILE" "$RUNTIME_DIR" || printf '诊断保存失败，继续核对回退状态。\n' >&2
    fi
    test -f "$RUNTIME_DIR/rollback-state.env" || { echo '没有本轮回退状态，停止并回报。' >&2; exit 1; }
    if [ -f "$log_dir/previous-rollback-state.env" ] && cmp -s "$log_dir/previous-rollback-state.env" "$RUNTIME_DIR/rollback-state.env"; then
      echo '回退状态未更新，不能拿上次状态自动回退；停止并回报。' >&2
      exit 1
    fi
    # Verify the saved state identifies the exact old images from this run.
    if ! (
      source "$RUNTIME_DIR/rollback-state.env" &&
      test "$PREVIOUS_BACKEND_REF" = "$previous_backend" &&
      test "$PREVIOUS_FRONTEND_REF" = "$previous_frontend" &&
      test -f "$BACKUP_FILE" &&
      test "$(docker image inspect --format '{{.Id}}' "$BACKEND_ROLLBACK_TAG")" = "$previous_backend_image" &&
      test "$(docker image inspect --format '{{.Id}}' "$FRONTEND_ROLLBACK_TAG")" = "$previous_frontend_image"
    ); then
      echo '回退状态不匹配，停止并回报；没有执行数据库还原。' >&2
      exit 1
    fi
    cp "$RUNTIME_DIR/rollback-state.env" "$log_dir/failed-rollback-state.env" || printf '状态副本保存失败；原 runtime 状态仍保留，继续应用回退。\n' >&2
    (umask 022; bash deploy/rollback.sh) 2>&1 | tee "$log_dir/rollback.log"
    printf '本轮升级失败，已回退应用；没有还原数据库。请回报输出，勿重复执行。记录：%s\n' "$log_dir" >&2
    exit 1
  fi
)
