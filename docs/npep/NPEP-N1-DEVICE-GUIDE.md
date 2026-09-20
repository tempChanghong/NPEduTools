# N1 设备端原型与隔离联调

适用范围：`codex/npep-n1-device` 开发分支。当前是可执行的设备适配器与验收工具，尚未接入 WPF 设置页、OOBE 或 Host 的常驻联网生命周期。现有主程序不会因为这些代码存在就自动连接学校服务器。

## 文件分工

| 位置 | 作用 |
| --- | --- |
| `src/NPEduTools.Integrations.Npep` | HTTPS 协议校验、DPAPI 凭据、双向配对确认、会话与只读状态上报、解绑 |
| `tools/NPEduTools.NpepProbe` | 需显式运行的设备侧命令行入口；不含学校管理员令牌 |
| `tests/NPEduTools.Npep.Tests` | 契约样例、错误响应、凭据保存、网络丢回执、撤销与状态映射测试 |
| `tests/NPEduTools.Npep.Acceptance` | 仅连接回环地址的真实跨端验收程序；使用测试学校管理员 fixture 完成双方操作 |
| `tests/NPEduTools.Tests/CachedHostStatusTests.cs` | 验证新缓存接口不会启动学校时间探测或课程读取进程 |

Host 新增 `host.cached-status` 本地管道请求，一次取得已有模式、录制和桥接缓存。`SchoolClockMonitor.PeekSnapshot()` 只取缓存；原有 `Snapshot()` 的工作方式不变。该接口没有云端请求处理器，也不赋予调用方新的软件控制操作。

设备适配器把缓存转换成协议允许的最小字段。文件路径、错误原文、录制计划、科目、端口、配对密钥均不出现在上报中。现有缓存没有桥接插件包版本，因此 `bridgeVersion` 为 null；不能拿 ExamAware 应用版本冒充插件版本。自动录制字段表示用户启用状态，即使考试模式暂时阻止录制也不擅自改成“未启用”。

## 常规测试与构建

在 Windows 仓库根目录执行：

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Npep.Tests -c Release
./scripts/dotnet.ps1 test tests/NPEduTools.Tests -c Release
./scripts/dotnet.ps1 build tools/NPEduTools.NpepProbe -c Release
./scripts/dotnet.ps1 run --project tools/NPEduTools.NpepProbe -c Release -- --help
```

DPAPI 依赖当前 Windows 用户。测试不会将凭据写入 NPEduTools 正式用户配置目录。锁文件用于排除同一凭据目录的并发实例；不应拷贝整个凭据目录到另一台大屏复用。

## 设备侧手动验收流程

以下命令参数是占位示例。`--data-dir` 每次使用同一个独立测试目录；服务地址必须是操作人员确认的 HTTPS origin，不能含用户信息、查询参数或路径前缀。

```powershell
./scripts/dotnet.ps1 run --project tools/NPEduTools.NpepProbe -c Release -- info --data-dir .artifacts/npep-manual --server https://school.example
```

1. 核对上一步展示的 `serverInstanceId` 和 `deploymentEpoch`。
2. 用 `pair --server <origin> --instance <UUID> --epoch <UUID> --name <设备名> --data-dir <目录>` 创建配对。设备先保存受 DPAPI 保护的配对候选，再发送请求。
3. 学校管理员在服务端用短码匹配申请、选择已有大屏绑定并批准；短码不等于设备授权秘密，不应贴到公共日志。
4. 用 `poll --data-dir <目录>` 读取学校、班级、大屏、能力与 approvalId，在本机核对。
5. 用 `confirm --approval-id <核对过的ID> --data-dir <目录>` 明确完成本机确认。
6. 用 `run --seconds 120 --pipe <已运行的测试Host管道> --data-dir <目录>` 上报。不会启动 Host、ClassIsland 或 ExamAware。未提供管道、旧 Host 不支持新接口或读取失败时，业务状态为 UNKNOWN。
7. 用 `unpair --data-dir <目录>` 停用并解绑。`REVOKED` 表示远端已确认撤销；`LOCAL_ONLY` 表示只确认本机凭据已清除，仍需管理员检查服务端登记。

每条命令都通过相同的 `dotnet run ... --` 前缀执行。工具没有跳过 HTTPS 证书验证选项，普通运行使用系统正常证书验证。

### 故障与恢复

| 本地状态/情形 | 行为与处理 |
| --- | --- |
| CREATING，创建回执丢失 | `resume-create` 使用原 requestId 和原配对候选，不另造申请 |
| CONFIRMING，激活回执丢失 | `recover` 先用已保存候选凭据读取 me；必要时重发完全相同的 confirm，不生成新 secret |
| ACTIVE | 新进程读取 me，再以 CAS 打开新会话；本进程开会话回执丢失时只重试原请求 |
| 上报超时 | 下一次读取新样本、递增序号并生成新 requestId；不积压或重传旧心跳 |
| 401/403/426、身份改变、会话被替换 | 保存 SUSPENDED 并停止上报，不循环争抢会话；需现场处理并重新配对 |
| UNPAIRING | 解绑开始后立即持久化此状态；即使进程中途终止，重启也不会恢复上报，可重试解绑 |
| 凭据损坏、不能解密、目录已占用 | 拒绝继续，不静默清空或重建凭据 |

采样年龄使用单调计时，发送前再次检查不超过 5 秒。网络请求整体超时 10 秒，包括响应体读取。成功上报间隔 20 秒；网络错误使用 5/10/20/40/60 秒退避并加少量抖动，尊重服务端 Retry-After。配对轮询为人工单步命令，服务端仍需执行其限流。

ClassIsland 的学校时间继续服务于课表和录制；N1 的授权、在线时间与过期判定使用服务端时间，不受手动对齐学校铃声影响。

## 真实跨端验收

KV 负责人负责准备专用 PostgreSQL、测试学校/班级/大屏、短期管理员会话和回环 HTTPS 服务。fixture 与证书放在 KV 已忽略的运行目录，不提交密钥或复制进本文。

```powershell
./scripts/dotnet.ps1 run --project tests/NPEduTools.Npep.Acceptance -c Release -- --fixture <隔离fixture.json绝对路径> --data-dir .artifacts/npep-e2e-new
```

此程序要求全新数据目录，自动完成隔离管理员批准和设备确认；它不能代替未来产品中的现场确认界面。只允许 HTTPS 回环地址，在专用 HTTP handler 内信任 fixture 的短期测试根，同时保留证书链、有效期和主机名校验，不更改 Windows 全局信任库。该测试程序不随应用安装包发布。

可额外传 `--pipe NPEduTools.Test.<名称>` 验证已有隔离 Host 缓存。默认未连接 Host 时上报真实的 UNKNOWN，而非编造日常/考试状态。验收输出只有检查项目和错误码，不输出管理员令牌、设备 secret 或原始异常内容。

下一阶段才实现 WPF 配对/停用界面、常驻运行生命周期和学校管理页，并完成完整生命周期、恢复闸门、跨学校权限与长时断网验收。N2 通知和 N3 模式切换不在本原型中开放。
