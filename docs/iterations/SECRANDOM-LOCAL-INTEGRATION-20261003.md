# SecRandom V3 本机接入

日期：2026-10-03。主交付物：**无需插件的课堂点名本机操作页**。实现阶段只修改 NPEduTools，不改 SecRandom 本体及其他仓库；随后按用户要求准备提交和 PR，记录见下方。

## 本轮实现

```mermaid
flowchart LR
    T[教师：主窗口或侧边栏] --> W[课堂点名页]
    W --> H[NPEduTools Host]
    H --> I[SecRandom 本机 IPC]
    I --> S[SecRandom V3：窗口、名单、抽取及历史]
    S --> I
    I --> H
    H --> W
```

| 部分 | 责任与入口 |
| --- | --- |
| App | `SecRandomWindow`：位置、检查、打开点名页、显示／隐藏浮窗、闪抽一人；主窗口和快捷侧边栏入口 |
| Contracts | `SecRandom.cs` 及 `Protocol.cs`：本机类型化命令、配置修订、操作编号和结果；不接受任意 URL |
| Host | `SecRandomService`：持久化意图和回执、并发控制、冷启动等待、结果不明后的人工核实 |
| 适配器 | `NPEduTools.Integrations.SecRandom`：服务端身份核对、有限长度 JSON 行协议、分别解析传输／业务结果 |
| 只读工具 | `NPEduTools.SecRandomProbe`：真实权限组合的探针，不触发抽取、启动或设置修改 |

接口固定于 v3.0.0 提交 `348ab0f506db86384c06b7d1d485b042128217f9`：`SecRandom_IPC_SecRandom_3F2A1B0E`，JSON 行 `version=1,type=url`。NPEduTools App → Host 使用既有长度前缀 JSON 协议，两者不可混用。

上游没有通用 ping，本机检查使用 `data/npedutools_probe?name=connectivity` 的预期 `invalid_command` 回执识别路由已就绪。这个探针不查询实际名单。上游外层 `success=true` 可能仍包含业务错误，必须继续检查内层 `result.status`。

闪抽持久化后只发送一次；同一操作编号在保留的 32 条记录中去重。断线、无效回执及 Host 重启后未完成的意图标记为待核实，不保证上游“恰好一次”，也不自动补抽。未解决记录不会被后续只读检查挤出。配置文件损坏保留原文件并停止新操作。

本机修改操作与既有课堂环境切换共用运行门闩；只读检查不占切换门闩。未加入考试联动、登录自启动、名单同步、NPEP 远程点名或插件事件订阅。

## 验证记录

| 层级 | 结果与证据 |
| --- | --- |
| 自动化专项 | 32/32 通过，无跳过；覆盖回执、帧上限、错误身份零发送、服务端退出、超时不重抽、并发、重启、去重、Host 管道路由；`.artifacts/secrandom-tests/aeaccdd69bf843ddbc21beb7576c8715/secrandom.trx` |
| 测试入口 | Windows PowerShell `5.1.26100.9444` 实际执行 `scripts/test-secrandom.ps1` 通过，`run.json` 记录源码状态和完成标志 |
| 项目与依赖 | App／Host 编译通过，零警告、零错误；解决方案 `--locked-mode` 恢复通过，App Debug 的 Host 目录已包含新适配器 |
| 完整 Host 回归 | 679/679 通过，无跳过；包含上述 32 项专项测试；`.artifacts/secrandom-regression/final.trx`，通过计数与逐项结果检查 |
| 真实普通客户端 → 普通实例 | 通过，`.artifacts/secrandom-live/ordinary.json`；2026-10-03 21:44:44 北京时间 |
| 真实管理员客户端 → 普通实例 | 通过，用户手动允许 UAC，`.artifacts/secrandom-live/elevated.json`；21:45:14 北京时间 |
| 正式版身份 | `C:\Great Apps\SecRandom-v3.0.0\app-v3.0.0-0\SecRandom.Desktop.exe`，产品版本对应上述提交 |
| 教师实际抽取与界面 | 未做；本轮未触发真实抽取，避免写入现有名单历史 |
| UIAccess／班级大屏／生产 | 未测试 |

开发用临时管道验证 App 使用的 Host 客户端 → Host 路由 → 模拟 SecRandom；真实探针验证适配器 → 正式版，不能合并表述成已通过真实 UI 全流程。

## 你回来后用 IDE 验收

1. 正常重启新版 App 和 Host，从“课堂点名 · SecRandom”进入；查找 URL 登记、核对并保存位置，检查接口。
2. 验证已有实例时打开点名页及浮窗；在 SecRandom 正常退出后验证“打开点名页”的冷启动。
3. 选好测试名单，闪抽一人；核对实际结果与回执一致、历史只增加一轮。保护验证若出现，应在 SecRandom 完成。
4. 正常退出 SecRandom 后检查接口应报告未就绪；不要以旧“就绪”时间戳当作实时状态。

简短使用说明见[课堂点名](../SECRANDOM.md)。

## 侧栏闪抽补充

主交付物：在收起后的常驻侧栏，设置分隔线上方加入“抽”按钮。栏体增加一格高度并同步 DPI／工作区定位；原“课堂点名 · SecRandom”入口继续管理设置。

未配置时引导保存程序位置；执行前读取新鲜的 Host 状态，已有操作或结果待核实时不提交。配置完毕后可冷启动 SecRandom，只读探针允许重试，真实抽取仍只发一次。按钮在等待最终回执期间禁用，异常打开操作记录。此次没有真实抽取，视觉和触摸验收由用户在 IDE Debug 环境完成。

新增回归：未配置零发送、冷启动探针重试后只抽一次、首次设置门控拒绝且不误报结果不明。专项脚本与结果文件沿用本任务入口，最新执行记录如下。

- 专项 35/35 通过，无跳过；Windows PowerShell 5.1 执行；`.artifacts/secrandom-tests/74115dcdab994badac6d31536bece889/secrandom.trx`。
- App／Host 编译零警告、零错误；侧栏高度从 284 调整到 340 DIP，定位与拖动使用同一新高度。
- 侧栏补充验收时尚未提交、推送或触发真实抽取；上述 679 项完整回归属于补充前记录。

## 提交前验收

用户授权 commit／PR 后，单独导出 Git 暂存区的完整源码到 C 盘，从独立副本验证：解决方案锁文件恢复通过，App／Host 与只读探针编译零警告、零错误，完整 Host 回归 **682/682** 通过且无跳过。逐项结果已由 `assert-test-results.ps1` 检查，本机记录保存于 `.artifacts/secrandom-pr-review/pr.trx`。

PR 仅包含 SecRandom 本机接入、侧栏闪抽、测试、锁文件及配套文档。自动录课页面、README 整理、Windows 安装包和生态架构草稿留在原工作区，不依赖这些未提交改动。真实抽取、触摸及班级大屏验收仍待用户完成；不自动合并或发布。
