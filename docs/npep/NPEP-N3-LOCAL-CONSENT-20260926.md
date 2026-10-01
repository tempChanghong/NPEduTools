# N3 本机许可与现场结束入口

日期：2026-09-26。

用户已回报上一阶段本机检查通过。本轮继续实现本机首次许可、状态查看与现场结束入口；**没有开放远程执行，没有更改或部署 NPClassworks / KV**。

## 当前行为

设置 → 学校互联 → 远程考试环境：

- 显示当前学校、班级、大屏和服务地址；默认不允许远程控制。连接正常后可开启一次“允许本校管理员远程切入考试环境”。
- 开关保存成功前不会显示为已授权；离线或暂停互联时仍能关闭已有许可。
- 暂停互联保留许可，同时更新控制代际。恢复互联后重新核验连接，旧请求不能凭原代际继续使用。
- 解绑、停止授权、设备绑定变化后关闭许可。重新配对不会继承上一配对的授权。
- 本机许可与 N2 通知接收无关，不改变原有状态上报和通知授权。
- 本页显示独立的 N3 自动录课暂停、最近八项执行记录及原始结果。不会以旧“日常／考试模式”的值代替 N3 状态。
- 有 N3 暂停时，先点“核实当前考试状态”，再由现场明确确认“结束本次远程考试状态”。实际结束时 Host 再次检查当前录制、权限、路径与软件管理状态；不会只相信前台检查结果。
- 结束只解除当前操作持有的 N3 暂停，保留原 outcome 和历史、标记本地结束时间，不启动或退出软件、不修改 Windows 自启动，不解除旧课堂模式等其他暂停条件。原计划可能录制当前剩余课时，界面与确认框均有说明。

**许可开关只是本地前置条件。当前没有 N3 轮询、服务器开始许可或远程 execute 入口，开启它不会立即让网页拥有执行能力。**

## 实现位置

| 位置 | 作用 |
| --- | --- |
| `src/NPEduTools.Contracts/RemoteExam.cs` | 本地 IPC DTO 和严格动作校验；不是 N3 0.3 网络协议 |
| `src/NPEduTools.Integrations.Npep/NpepControlPolicyStore.cs` | 独立许可文件、修订、consentId 和 controlEpoch |
| `src/NPEduTools.Integrations.Npep/NpepRuntime.Control.cs` | 当前配对指纹、本地开关、连接条件与说明 |
| `src/NPEduTools.Host/PipeServer.RemoteExam.cs` | 本地状态查询、许可更新、本地结束；不暴露 run |
| `src/NPEduTools.Host/RemoteExamExecutor.cs` | 新增未解决操作的只读核实，继续复用原有现场结束检查 |
| `src/NPEduTools.App/MainWindow.RemoteExam.cs` | 状态刷新、开关确认、检查和现场结束交互 |

许可文件位于 Host 数据目录的 `npep/runtime-control-policy.json`，默认对应 `%LOCALAPPDATA%/NPEduTools/config/npep/`。不写入设备密钥、配对密钥或 bearer。

配对指纹使用确定性 JSON 数组的 SHA-256，包含 origin、serverInstanceId、deploymentEpoch、deviceId、screenBindingId、bindingRevision、credentialGeneration、approvalId。TLS 临时故障不改变指纹，不丢失许可；不可恢复的授权停止会清除许可。

每次开关变化更新 consentId 与 controlEpoch；Host 启动和受理互联状态操作也更新 controlEpoch。修订号随持久化增加。重新启动保留同一配对的许可，但不会复用旧控制代际。

文件采用 `.pending` 写入、`Flush(true)` 和移动替换；发现损坏或遗留 `.pending` 时禁用控制并保留文件，不静默重置成可执行状态。许可文件问题不阻止 N1/N2 原功能，但不能继续开启 N3。

本地 IPC 新增 `remoteexam.status`、`remoteexam.inspect` 和 `remoteexam.command`。后者仅允许 `consent` / `end-local`，拒绝任意路径、自启动参数、混合参数及未知动作。未来服务端不得将 `end-local` 包装成远程回日常接口。

## 验证记录与边界

已完成：14 个相关 C# 文件的 Roslyn **语法解析**、XAML XML 解析及名称唯一性检查、Git whitespace 检查。语法解析不等于编译或运行验证。

新增测试源码（未执行）：

- `ControlPolicyTests`：默认关闭、重启保留许可且更换代际、关闭重开、绑定隔离、旧修订拒绝、损坏／中断写入保留证据、协议拒绝执行和混合参数。
- `RuntimeTests.Control`：实际 Runtime 的配对、开启、暂停、离线关闭、恢复、解绑及不发送 N3 请求。
- `RemoteExamTests.LocalInspectionOfPartialResultDoesNotResolveOrReplayIt`：部分完成后的检查不清账本、不重放；旧修订不能结束；现场结束保留原结果且没有进程动作。

按用户约定，本轮未执行 build、test 或 publish，未打包 ZIP，未操作真实软件退出和学校配对。以上单元测试需由 IDE 构建执行；新界面和完整交互尚待现场 Debug 验证。

## IDE 验收

1. 更新 Debug 运行实例及 Host，打开设置 → 学校互联。确认学校信息正确，首次默认关闭。
2. 连接正常时开启许可，切换页面后再回来，确认仍开启。
3. 暂停互联，确认许可保留、状态提示当前不可控制；此时仍可关闭许可。
4. 恢复互联，确认不会自动重新打开刚关闭的许可。
5. 再开启并正常退出全部 NPEduTools；重启后，同一配对的开关应保留，连接确认前不显示可用于控制。
6. 当前没有真实 N3 操作时，“结束”按钮保持禁用。这是预期，不应伪造运行账本来让按钮可点。

不要为验证配对隔离而随意解除生产设备绑定；该项先用隔离测试覆盖。现场结束的真实流程须等待 N3 投递接通后验收。

## 下一阶段

与 NPClassworks/KV 冻结 N3 0.3 协议，再实现独立许可镜像、操作轮询、短期开始许可和幂等回执。执行授权必须同时核验当前绑定、policyRevision、consentId、controlEpoch、有效账号／角色、期限及 runtimeRevision；本地 Allowed 或 CanEnable 不能直接充当执行许可。接通后在单台测试设备验收正常执行、重复投递、权限关闭、录制忙及部分失败。
