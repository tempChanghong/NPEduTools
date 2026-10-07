# 河豚豚的服务器侦察手册

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

> 密令：先看地形，再开大门。此行只收集情报，暂不升级。

河豚豚，这次只需要你在服务器上查看几个信息，把结果发回来。**不用分析那些长串编号，也不用现在修东西；看不懂的输出交给 Codex。**

两个据点已经确认：`/NPClassworks` 放网页前端，`/NPClassworksKV` 放后端服务。河豚豚已确认维护时间灵活、生产配置使用默认路径。首次备份现已完成：`/root/npclassworks-before-npep-fyhzmkZG`，328M，文件校验均 OK。随后 [隔离恢复](NPEP-N1-RESTORE-DRILL.md) 成功恢复 31 张用户表，临时资源已清理，不用重跑。异地备份也已确认完成，PM2 部署代理为 `np-deploy-agent`（ID 4，online）。下一步准备最终受测版本与受控发布，以下保留此前核查记录。

我们已经约好：以后先更新程序，暂不开启 NPEP 连接；确认作业、通知等原有功能正常，再另外找一台大屏试用。下面这些命令只是更新前的查看步骤，不是更新命令。

## 第一封情报：服务器现在用的是哪一版？

在**服务器终端**里，用平时管理项目的账号复制运行这一整段。不是在个人电脑的 PowerShell 里运行。

```bash
(
  set -eu
  for repo in /NPClassworks /NPClassworksKV; do
    printf '\nRepository: %s\n' "$repo"
    git -C "$repo" rev-parse --show-toplevel
    git -C "$repo" rev-parse HEAD
    git --no-optional-locks -C "$repo" status --short
  done
)
```

每个目录下面那串长长的字母和数字，就是代码的版本编号。后面若还有 `M`、`??` 等行，表示目录里有本地改动。**把输出原样发回来即可，不用自行清理。**

如果出现“没有这个目录”“没有权限”或 Git 所有权提示，也直接反馈。先别改权限、删文件或重置代码。这段命令只查看，不会拉取或更新程序。

## 第二封情报：哪些服务正在运行？

继续复制运行这一段：

```bash
docker ps -a \
  --filter 'label=com.docker.compose.project.working_dir=/NPClassworksKV' \
  --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Label "com.docker.compose.project"}}\t{{.Label "com.docker.compose.service"}}'
```

它会列出属于这个部署目录的服务名称、镜像和状态，不会启动或停止服务，也不显示密码。把这张表发回来就行。

**如果只有表头，也原样发回。** 可能是实际启动时用了别的目录，不等于网站坏了；先不要为了让表里“有东西”而重启或重新部署。

## 第三封情报：更新机关有没有在忙？

河豚豚已确认：部署代理监听本机 **17020**，由 OpenResty 提供 HTTPS 入口 `https://deploy.newfires.top`。后续使用这个入口，不使用模板默认端口，也不需要把 17020 开放到公网。自动部署程序会报告自己是否正在更新、还有几次更新排队：

```bash
curl --proto '=https' --fail --silent --show-error --max-time 15 https://deploy.newfires.top/healthz
```

结果里的 `busy` 表示是否正在更新，`queued` 表示排队数量。`busy=false`、`queued=0` 意思是查询的这一刻没在忙；真正更新前我们还会再核对。

Codex 已于北京时间 2026-09-21 20:24 通过此入口取得 HTTP 200 和 `{"ok":true,"busy":false,"queued":0}`。这一项暂时不用重复查；正式更新前再查一次。它是部署代理的状态，不等于作业、通知等业务已经验收。连接失败时也不要跳过 TLS 校验。这条命令只询问状态，不会触发更新。

## 最后捎句话，不用交出钥匙

以下各项现已收到，不用再次填写：

```text
我方便维护的日期、时间（北京时间）：河豚豚表示随时可以配合；正式变更仍协调一次开始时间
现在网页、作业等功能是否正常：用户确认截至 2026-09-21 18:30（北京时间）正常；维护前再复查
第一、二项命令的输出：已收到，无需重复
部署程序状态：已通过 HTTPS 查询；正式更新前复查

生产配置文件的位置：/NPClassworksKV/deploy/.env.production（已确认）
DEPLOY_MODE 的值：shared（已确认，无需修改）
NPEP_ENABLED 的值：未设置（用户确认，无需再询问或现在添加）
现有数据库备份和旧版程序镜像：首次存档及数据库隔离恢复通过；异地备份已完成，目标设备校验未单独回报
```

`DEPLOY_MODE` 是部署方式，`NPEP_ENABLED` 是互联开关；这里只问当前值，**不用修改它们**。旧程序没有互联开关也正常。旧版镜像和数据库备份是更新出问题时可能用到的退路；不知道是否可用就如实说，不能当作已经检查过。

只发上述信息即可。密码、SSH 私钥、令牌和整个 `.env` 文件不用发。钥匙留在你手上。

## 情报送达后，谁来做什么？

- **河豚豚**：提供查看结果和维护日期；后续按准备好的步骤操作服务器。
- **Codex**：判断版本、核对备份和恢复条件，准备具体更新步骤，协调 GitHub 端的操作。
- **用户本人**：可以不全程在场，已经同意的分阶段路线不用再重复选择。

现在不需要合并 `main`、执行 `git pull`、运行生产部署或打开 NPEP。部署完成也不会立即让所有大屏连接上来；大屏试点另行安排。

我们还要核对故障时怎么恢复：现有自动回滚不是所有情况下都有效，后端完全停机时的离线数据库恢复也尚未验收。信息不足的地方会先补齐，再给正式更新步骤。

## 已收到的情报（2026-09-21）

- `/NPClassworks`：`19756f654f94006084d1d954b8be4171db7c9a18`；`/NPClassworksKV`：`f731f3227a7c59585aff940f78354585d3b016b7`。两仓 `status --short` 均无输出，工作树干净，与此前记录的旧版基线一致。仓库 HEAD 不单独证明运行镜像对应同一提交。
- Compose 项目 `npclassworks`：backend、frontend 均运行约 22 小时且 healthy，postgres 运行约 4 周且 healthy。镜像分别为 `npclassworks-backend:current`、`npclassworks-frontend:current`、`postgres:17-alpine`。这是河豚豚提供时的快照。
- 部署模式为 `shared`，现场仍有本项目 PostgreSQL 容器；不改成 standalone。
- 用户补充确认：`NPEP_ENABLED` 未设置；作业、通知、学校管理等原有业务截至 2026-09-21 18:30（北京时间）正常。这是用户提供的业务状态，非 Codex 现场验收；后续维护前后仍需复查。此次核查期间 Codex 仅查询状态和修改本地文档，未推送或触发部署。
- 河豚豚已确认维护时间灵活、生产配置在默认路径，并回报首次存档、隔离恢复及异地备份完成，见 [备份记录](NPEP-N1-FIRST-BACKUP.md)。部署代理实际由 PM2 管理，应用名 `np-deploy-agent`、ID 4、online。最终受测版本准备完成前不提前暂停。暂停与恢复应使用同一 PM2 用户及应用名，不使用 stop all/delete/save；正式窗口先控制 GitHub 发布任务、排空队列，再安排定向暂停和恢复。

**信息收集已完成；先留存档，再做恢复核验和受控发布。**
