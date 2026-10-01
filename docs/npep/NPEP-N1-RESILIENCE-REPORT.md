# NPEP N1 断线与进程恢复验收

日期：2026-09-21。设备基线 `e360ff2`；本轮功能分支 `codex/npep-n1-resilience`。本轮补充验收程序和现场文档，没有修改产品运行代码或增加 N1 权限。

## 结论与范围

在开发电脑上完成真实回环 HTTPS + PostgreSQL 的网络故障注入，以及独立测试进程的正常退出、突然终止和重新连接。既有凭据仍绑定同一设备；离线不更新云端观测；恢复发送新样本；新进程打开更高代际的状态会话。没有用客户端时间伪造超过 60 秒在线窗口。

当前电脑不是班级大屏。上述结果不能代替真实大屏网卡、睡眠、Windows 重启、WPF 双端人工配对、生产代理或数据库恢复运维验收。可直接填写的步骤见 [现场验收与上线前检查](NPEP-N1-FIELD-ACCEPTANCE.md)。

## 本轮验证

| 项目 | 结果与证据边界 |
| --- | --- |
| NPEP 单元/专项回归 | 86/86 通过；既有凭据、协议、后台生命周期测试 |
| 验收程序 Release 构建 | 通过，0 警告、0 错误 |
| 真 HTTPS + PostgreSQL 验收 | 26/26 通过：此前 17 项，加本轮 9 项；使用当前正式 NpepRuntime 与 NpepDevice，非另写的重连实现 |
| 共享 wire schema | 设备 `n1-wire.schema.json` 与 KV `domain/npep/wire.schema.json` 无差异 |
| 生产公开 info | 正常系统 TLS 通过；网页 origin 返回 HTML，API origin 返回 404，尚不能配对 |
| WPF / 原有其它功能 | 本轮无产品代码改动，不重复执行上一阶段 WPF 人工检查和 352 项其它回归；其结果归属上一阶段 |

本轮 9 项具体检查：

1. 已配对但断网启动：保留 ACTIVE 本地凭据，显示 OFFLINE，不把上次运行的回执冒充新回执。
2. 测试传输断开 65 秒，实际等待服务端在线窗口过期：学校查询为 OFFLINE，lastSeenAt 与断线前相同。
3. 恢复网络后不点击恢复、不创建配对：后台按退避自动连回，同一 deviceId，收到恢复后的采样标记。
4. 重启的 Runtime 使用新状态会话，序号从 1 开始；服务端正常接受，未被旧序号覆盖。
5. 下次状态已被真实服务器接收后，测试 handler 丢弃成功响应：学校端已看到该次采样，客户端看到网络失败。
6. 下次自动上报使用更大 sequence、新 requestId、同一 sessionId 和更新后的采样标记；lastSeenAt 前进，不重放旧心跳。
7. 独立子进程使用当前用户 DPAPI 读取原目录，同一设备打开更高 statusEpoch，学校端看到新接收时间。
8. 前一子进程正常退出后，再启动子进程：原凭据、目录锁和会话恢复成功。
9. 仅强制终止上述测试子进程，再启动一个子进程：操作系统释放锁，原凭据仍可读取、会话代际继续前进，无重复 ACTIVE 登记。

随后继续原验收中的管理员撤销、Runtime 停用和 LOCAL_ONLY 本机清理，避免把仍可用的测试设备留在后台。

测试采样是明确标记的隔离状态，`modeRevision` 只用于区分新旧样本，没有真实切换课堂模式、启动录制或操作 ClassIsland/ExamAware。

## 复现与隔离

在 Windows、已有 SDK 和专用 PostgreSQL 环境下，由服务端任务准备回环服务及 ignored fixture，然后执行：

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests -c Release
./scripts/dotnet.ps1 run --project tests/NPEduTools.Npep.Acceptance -c Release -- --fixture <隔离fixture.json绝对路径> --data-dir <全新隔离目录>
```

完整验收现在通常需要数分钟，包含实际 65 秒等待与自动退避；不要把它作为秒级单元测试。可选 `--pipe NPEduTools.Test.<名称>` 的既有 Host 缓存检查另外计 1 项，本轮未传此参数。

- `ResilienceAcceptance.cs` 中故障层仅包裹验收专用 HTTPS handler，未进入正式 App/Host；不修改网卡、防火墙、时钟或系统证书库。
- `--runtime-worker` 是验收程序自己的内部子进程入口，固定 loopback fixture、预期 deviceId 和独立目录；不是正式 Host 命令。父进程仅终止自己创建的子进程，标准输出只包含 ONLINE 与状态代际，不打印凭据或请求 body。
- 测试客户端使用专用短期根的 CustomRootTrust，并校验主机名、证书链和有效期；生产公开接口检查使用系统正常 TLS，未添加信任或跳过验证。
- 本轮在 C 盘隔离副本运行，KV 使用既有专用 `npclassworks_test_npep` 库和 `localhost:34439`；不接触生产库或用户正式应用配置。
- 结束后停止本轮创建的 HTTPS 进程和测试子进程，保留专用 PostgreSQL 与 ignored 夹具，不关闭 Docker/WSL 或删除数据库卷。设备测试凭据已走撤销/本机清理。

## 与服务端协作及下一步

前端文档提交：`NPClassworks/codex/npep-n1-admin-ui` 的 `4d10bb3`；后端部署准备提交：`NPClassworksKV/codex/npep-n1-server` 的 `c61859b`。这些是本地功能分支提交，没有推送或部署。

用户明确 **main 会触发 server.js 自动部署**。NPEP 尚在功能分支，线上 API 404 与未发布背景一致，不作为部署故障报告。当前使用域名、只读观测及现场操作流程见现场表，Docker 配置缺口见 [服务端部署准备记录](../../../NPClassworksKV/docs/NPEP-N1-DEPLOYMENT-READINESS.md)。

下一步应先补齐可审核的生产 Compose 开关/目录挂载、恢复环境一致性和显式 N1 CI 门槛，验证关闭状态下的部署配置；审核发布顺序后再合入 main。正式 info 就绪后才能进行真实大屏上的人工配对及现场验收。本轮不推进 N2 通知或 N3 模式指令。
