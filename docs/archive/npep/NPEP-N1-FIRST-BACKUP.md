# 河豚豚：先留存档，再开机关

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

2026-09-21。最初河豚豚确认没有备份，现已按本页完成首次存档。本页依据服务器当前 KV 提交 `f731f3227a7c59585aff940f78354585d3b016b7` 中的 `deploy/backup.sh`、`deploy/lib.sh` 和 `docker-compose.shared.yml` 编写，不要求先拉新代码。以下命令保留备查，本次不用重跑。

## 已收到的执行结果

- 操作者回报存档：`/root/npclassworks-before-npep-fyhzmkZG`，总大小 `328M`。
- 数据库：`NPClassworksKV`；原始 dump 为 `/NPClassworksKV/deploy/backups/npclassworks_NPClassworksKV_20260921T132825Z_before-npep.dump`，文件名时间对应北京时间 2026-09-21 21:28:25。
- 原始 dump 校验以及存档中 database.dump、database.dump.meta、running-images.tar、production.env、docker-compose.shared.yml、images.tsv、backend.commit、frontend.commit 均回报 OK。
- 后续河豚豚已完成隔离恢复：还原出 31 张用户表，临时容器及匿名数据卷已清理，记录目录 `/root/npclassworks-before-npep-fyhzmkZG/restore-check-cng0O6eV`。详见 [隔离恢复记录](NPEP-N1-RESTORE-DRILL.md)。
- 结论：首次备份、文件校验及数据库隔离恢复完成（依据操作者输出，Codex 未读取服务器文件）。河豚豚随后确认异地备份已完成；未单独回报目标设备的 SHA256 校验结果，不记为目标校验已通过。不将数据库演练等同于整套生产回退验收。

## 操作前

这次会在线导出数据库、复制生产配置并保存正在运行的三个容器所用镜像，不停止服务。期间先不要合并 main、手动部署或清理镜像；如有其他管理员，请约好这段时间不做这些操作。开始前查看 `https://deploy.newfires.top/healthz`，只有 `busy=false`、`queued=0` 才继续；这个快照不代替正式升级时对自动部署入口的暂停。

在服务器执行 `df -h /root /NPClassworksKV` 查看可用空间。镜像包可能较大，空间明显不足时先停下反馈，不清理数据库或现用镜像。备份也会占用一些 CPU 和磁盘带宽。

## 复制到服务器 Bash 执行

当前终端是 root；新建目录位于 `/root`，仅 root 可访问。失败会停止并保留已产生的文件，不继续升级。不要开启 `set -x`，也不要把生成的文件上传到聊天中。

```bash
(
  set -Eeuo pipefail
  umask 077
  cd /NPClassworksKV
  test "$(git rev-parse HEAD)" = f731f3227a7c59585aff940f78354585d3b016b7
  test "$(git -C /NPClassworks rev-parse HEAD)" = 19756f654f94006084d1d954b8be4171db7c9a18
  test -z "$(git --no-optional-locks status --porcelain)"
  test -z "$(git --no-optional-locks -C /NPClassworks status --porcelain)"
  test -f deploy/.env.production

  snapshot=$(mktemp -d /root/npclassworks-before-npep-XXXXXXXX)
  printf '存档目录：%s\n' "$snapshot"
  containers=(npclassworks-backend-1 npclassworks-frontend-1 npclassworks-postgres-1)
  image_ids=()
  for container in "${containers[@]}"; do
    test "$(docker inspect --format '{{.State.Running}}' "$container")" = true
    image_id=$(docker inspect --format '{{.Image}}' "$container")
    image_ids+=("$image_id")
    printf '%s\t%s\n' "$container" "$image_id" >> "$snapshot/images.tsv"
  done
  git rev-parse HEAD > "$snapshot/backend.commit"
  git -C /NPClassworks rev-parse HEAD > "$snapshot/frontend.commit"
  cp deploy/.env.production "$snapshot/production.env"
  cp docker-compose.shared.yml "$snapshot/docker-compose.shared.yml"
  if test -d deploy/runtime; then
    cp -a deploy/runtime "$snapshot/runtime"
  fi

  backup_file=$(ENV_FILE=/NPClassworksKV/deploy/.env.production bash deploy/backup.sh --label before-npep --retention-days 0)
  test -s "$backup_file"
  (cd "$(dirname "$backup_file")" && sha256sum --check "$(basename "$backup_file").sha256")
  cp "$backup_file" "$snapshot/database.dump"
  cp "$backup_file.meta" "$snapshot/database.dump.meta"

  docker image save --output "$snapshot/running-images.tar" "${image_ids[@]}"
  test -s "$snapshot/running-images.tar"
  tar -tf "$snapshot/running-images.tar" > /dev/null
  for index in "${!containers[@]}"; do
    test "$(docker inspect --format '{{.Image}}' "${containers[$index]}")" = "${image_ids[$index]}"
  done
  (
    cd "$snapshot"
    sha256sum database.dump database.dump.meta running-images.tar production.env docker-compose.shared.yml images.tsv backend.commit frontend.commit > SHA256SUMS.txt
    sha256sum --check SHA256SUMS.txt
  )
  printf '\n存档完成：%s\n' "$snapshot"
  du -sh "$snapshot"
)
```

`--retention-days 0` 禁止本次调用清理旧备份。脚本原始数据库备份仍留在配置的 BACKUP_DIR（默认 `/NPClassworksKV/deploy/backups`），另复制一份到独立存档目录。Docker 按运行容器实际镜像 ID 导出，不依赖 `:current` 标签当时指向；`images.tsv` 保留容器与镜像 ID 对应，后续恢复需据此恢复标签。运行前后镜像发生变化会停止，不能把那次存档直接当成稳定发布基线。

校验通过仅说明数据库备份目录可读取、文件校验一致和镜像 tar 可列出，**不是完整恢复演练已通过**。生产配置有密钥，数据库有业务数据，镜像也应作为私有材料保管。再由河豚豚把整个存档目录复制到受控的另一台设备或备份存储，并在那里核验 SHA256SUMS；只留同一块服务器磁盘不能防磁盘故障。runtime 是附带的历史记录，文件清单校验不包含其中的嵌套文件。

## 发回这些就行

- 命令是否完成；若失败，发错误信息，不发配置内容。
- 存档目录路径、最后的校验结果和总大小。
- 是否已保留异机副本。

收到后由 Codex 准备隔离恢复验证与正式升级步骤。尚未备份宿主 OpenResty/systemd 等外部配置，本轮也不修改这些配置。本次在线数据库快照只包含备份时的数据，不能替代正式升级前再备份一次；与第三方系统的数据也不构成跨系统事务快照。

## 如果河豚豚使用自己的 Codex

可把本页和 `NPEP-N1-RELEASE-PLAN.md` 交给其 Codex 作为交接材料，并说明只执行备份、核验和回报。另一段对话不能被视为已自动同步当前上下文，也不默认获得这里的服务器访问权限。回报至少带上两仓 SHA、执行命令、是否成功及下一步；不得发送密钥或数据库。正式升级继续沿用“先更新但关闭 NPEP，验收业务，再单屏试点”的约定。
