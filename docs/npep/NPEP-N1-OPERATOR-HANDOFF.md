# 给服务器操作者：NPEP N1 更新前核查

更新日期：2026-09-21。本文可直接转发给负责服务器的朋友。

## 已确认

- 前端仓库：`/NPClassworks`，网页为 `https://newfires.top`。
- 后端仓库：`/NPClassworksKV`，API 为 `https://api.newfires.top`。
- 操作者：用户的朋友；用户本人不一定在场。
- 可维护时段：北京时间 19:00 以后，**具体日期尚未约定**。
- 已同意的路线：先更新代码、保持 NPEP 关闭，检查原有业务；之后再单独启用一台大屏试点。

以下只读核查可以先做。本文不是立即升级指令，也不需要账号密码、SSH 私钥、部署密钥或完整 `.env` 内容。

## 现在可以执行的只读检查

在服务器终端中，用平时管理这些项目的账号运行。发生权限错误、目录不存在或 Git 所有权提示时，保留错误并反馈，不据此改权限、改仓库配置或新建目录。

### 1. 确认服务器上的实际代码

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

这段命令不会拉取、切换或更新代码。每个仓库最后的 `status` 没有输出表示工作区干净；如果有输出，先反馈，不要删除修改或执行 reset。请回传实际 HEAD 与是否干净，不要将开发电脑上的提交号作为服务器当前版本。

### 2. 确认这套 Compose 的容器

```bash
docker ps -a \
  --filter 'label=com.docker.compose.project.working_dir=/NPClassworksKV' \
  --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Label "com.docker.compose.project"}}\t{{.Label "com.docker.compose.service"}}'
```

输出仅包含该工作目录标签下的容器名称、镜像、状态、项目名和服务名，不输出容器环境变量。如果只有表头，不代表程序停机；可能现场使用了不同的 Compose 工作目录。请说明实际启动方式，由我们继续核对，不要运行 `compose up` 来“补齐”。

### 3. 由操作者确认的配置与状态

仅反馈以下值或结论，不发送完整配置：

| 项目 | 反馈内容 |
| --- | --- |
| 生产 env 的实际路径 | 脚本默认 `/NPClassworksKV/deploy/.env.production`；若使用覆盖路径，请说明实际路径 |
| `DEPLOY_MODE` | shared / standalone / 未设置；不要仅凭网页与 API 域名分开推断 |
| `NPEP_ENABLED` | true / false / 未设置；核对现场值，不先修改。旧版不存在这个变量并不异常 |
| 部署代理 | 是否空闲、是否有排队任务 |
| 当前业务 | 网页、作业、大屏等当前是否正常 |
| 回退材料 | 当前是否有可核验的数据库备份及原前后端镜像，回答情况即可；不用传数据库文件 |

仓库中的代理提供只读 `GET /healthz`，返回 `busy` 和 `queued`。**如果朋友确认现场沿用默认端口 19090**，可在服务器本机执行：

```bash
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:19090/healthz
```

若现场端口不同，使用操作者已知的实际本机地址；不要猜测或扫描端口。连接失败只能说明本次未取得代理状态，不代表业务服务异常。即便此刻 `busy=false`、`queued=0`，正式维护前仍须控制发布入口后重新确认。

## 回传模板

```text
可维护日期与时间（北京时间）：
前端 HEAD：
后端 HEAD：
两仓工作区是否干净：
Compose 项目名、实际部署方式：
生产 env 路径（不附内容）：
DEPLOY_MODE：
NPEP_ENABLED：
代理 busy / queued（或尚未取得状态）：
当前业务是否正常：
数据库备份及原镜像是否可核验、保留：
```

## 收到核查结果之后

我们先核对服务器当前版本、脚本和恢复条件，再完成维护窗口的具体操作单。用户已经同意“先关闭状态更新”的路线，不需要反复确认这一选择；目录确认本身不等于可以忽略尚未确定的维护日期与现场条件。

正式操作顺序为：控制两仓生产部署及相关镜像发布入口 → 排空旧请求并保留回退基线 → 合入前后端并验证最终两个 SHA → 使用现有脚本只发布一次确定版本 → 检查原业务与 NPEP 关闭状态。具体 GitHub 操作可由 Codex 侧处理，服务器操作由朋友负责，在准备就绪后明确分工。

当前先不要点击合并 main、手动运行生产部署 workflow、执行 `git pull` 更新生产代码或调用 `/v1/deploy`；也不要初始化或激活 NPEP。本阶段无需修改 ClassIsland、ExamAware 或大屏自启动。

已有应急限制仍需处理：自动回滚并不覆盖全部构建/启动故障；后端已停止时的完整离线数据库恢复尚未验收。未核对回退条件前，不把旧报告中的功能分支 SHA 当成可以立即执行的发布命令。
