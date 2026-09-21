# 河豚豚：试读存档，不碰线上数据库

## 已完成的现场验证

河豚豚已回报执行成功：八项存档校验均 OK，数据库隔离恢复通过，用户表数量 **31**，临时容器及匿名数据卷已清理。验证记录位于 `/root/npclassworks-before-npep-fyhzmkZG/restore-check-cng0O6eV`。结论依据操作者提供的终端输出；Codex 未直接访问服务器或读取业务数据。

本项无需重复执行。河豚豚随后确认异地备份完成，部署代理由 PM2 管理，应用名 `np-deploy-agent`，ID `4`，回报状态 `online`。异地副本目标校验未单独回报。下一步控制自动部署入口及确定最终受测版本，目前代理继续运行。以下保留已执行步骤供追溯，不表示应用回退、镜像存档加载或完整离线恢复也已验收。

目的：把已备份的数据库真正还原到一个临时 PostgreSQL 17 容器，确认备份可用。备份校验已经完成，这一步只做恢复演练，不部署新版。

使用存档记录的 PostgreSQL 镜像 ID，在现有服务器上创建独立容器及其匿名数据卷；不连接 Docker 网络、不映射端口、不挂载任何线上数据卷、不读取生产 env。演练结束只删除本次临时容器及其匿名卷，存档与线上容器保留。会使用服务器磁盘、CPU 和最多 512 MiB 容器内存；资源紧张时另约空闲时段，或改在河豚豚控制的其他 Docker 主机上演练，不硬挤线上资源。

先查看 `df -h /var/lib/docker /root`（Docker 数据目录若自定义，查看实际目录）和 `free -h`。备份大小不等于恢复所需空间，数据与索引展开后可能更大。执行期间保持不升级、不清理镜像。

下面在服务器 Bash 中执行。任一步失败就停下，不继续升级。恢复日志可能含业务内容，因此仅回传成功摘要；若失败，先检查日志并隐去业务数据后再提供错误信息。

```bash
(
  set -Eeuo pipefail
  umask 077
  snapshot=/root/npclassworks-before-npep-fyhzmkZG
  cd "$snapshot"
  sha256sum --check SHA256SUMS.txt
  pg_image=$(awk -F '\t' '$1 == "npclassworks-postgres-1" {print $2}' images.tsv)
  [[ "$pg_image" =~ ^sha256:[a-f0-9]{64}$ ]]
  docker image inspect "$pg_image" >/dev/null
  run_dir=$(mktemp -d "$snapshot/restore-check-XXXXXXXX")
  container_id=''
  cleanup() {
    if [[ -n "$container_id" ]]; then
      docker rm --force --volumes "$container_id" >/dev/null || {
        printf '临时容器清理失败，请反馈 ID：%s\n' "$container_id" >&2
        return 1
      }
    fi
  }
  trap cleanup EXIT
  container_id=$(docker create \
    --name "npep-$(basename "$run_dir")" \
    --label npclassworks.purpose=restore-drill \
    --network none --cpus 1 --memory 512m \
    --env POSTGRES_USER=restore_check \
    --env POSTGRES_DB=npep_restore_check \
    --env POSTGRES_HOST_AUTH_METHOD=trust \
    "$pg_image")
  printf '%s\n' "$container_id" > "$run_dir/container-id.txt"
  docker start "$container_id" >/dev/null
  ready=false
  for attempt in {1..60}; do
    if docker exec "$container_id" pg_isready -h 127.0.0.1 -U restore_check -d npep_restore_check >/dev/null 2>&1; then
      ready=true
      break
    fi
    sleep 1
  done
  if [[ "$ready" != true ]]; then
    docker logs "$container_id" > "$run_dir/startup.log" 2>&1
    printf '临时数据库未就绪，日志留在 %s\n' "$run_dir" >&2
    exit 1
  fi
  if ! docker exec -i "$container_id" pg_restore \
    --username restore_check --dbname npep_restore_check \
    --no-owner --no-privileges --exit-on-error --single-transaction \
    < database.dump > "$run_dir/restore.log" 2>&1; then
    printf '恢复未通过，日志留在 %s；先勿升级。\n' "$run_dir" >&2
    exit 1
  fi
  table_count=$(docker exec "$container_id" psql -X -U restore_check -d npep_restore_check -At -v ON_ERROR_STOP=1 \
    -c "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema');")
  [[ "$table_count" =~ ^[0-9]+$ ]] && (( table_count > 0 ))
  printf '数据库隔离恢复通过；用户表数量：%s\n' "$table_count" | tee "$run_dir/result.txt"
  cleanup
  container_id=''
  printf '临时容器及匿名数据卷已清理；记录目录：%s\n' "$run_dir"
)
```

`trust` 仅用于这次无网络、无映射端口的临时数据库，不修改生产认证配置。使用 `--single-transaction` 和 `--exit-on-error`，恢复过程中有错误不会报成功。清理命令只使用 `docker create` 返回的本次容器 ID，禁止替换成生产容器名或使用 prune/down。

若本地镜像 ID 不存在，停止反馈，先安排在独立 Docker 环境验证镜像存档；不要为此自动拉取不同版本镜像或覆盖现用标签。

完成后发回最后两行：表数量、临时容器已清理和记录路径。同时告知 328M 存档是否已有异机副本；若尚未完成，说明即可，不把同盘副本当异机备份。

这一步通过表示 dump 可在对应 PostgreSQL 镜像中恢复，不证明新版本业务正常、旧程序兼容新迁移或整套离线恢复已验收，也未验证从 tar 加载所有应用镜像。正式上线前仍需明确应用回退路径、控制自动部署入口并确认最终受测提交，部署前再做新备份。
