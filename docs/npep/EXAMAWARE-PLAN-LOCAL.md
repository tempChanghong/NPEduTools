# 考试方案：本机校验与放映

2026-09-27 · NPEduTools + ExamAware 1.5.2 + 桥接 0.4.0

```mermaid
flowchart LR
    A[用户选择本地方案 JSON] --> B[NPEduTools 读取文件快照]
    B --> C[桥接调用官方 player.prepare]
    C --> D[显示考试名称、场次、时间与提示语]
    D --> E[用户确认开始放映]
    E --> F[官方 startFromConfig\n禁止替换已有放映]
    F --> G[官方 listSessions\n读回准备、就绪、关闭状态]
```

本文记录本机闭环；后续已在本地接入 [学校投递考试方案](EXAMAWARE-PLAN-REMOTE.md)。N3 切换考试模式仍不会自动播放方案。选择方案不修改 ExamAware 内部配置文件，不改 Windows 自启动。

## 你怎么试

1. 在 ExamAware 插件设置安装 `.artifacts/examaware-bridge/npedutools-examaware-bridge-0.4.0.ea2x`，确认 `player.start`、`player.observe` 新权限；只保留一个启用的桥接。原 v2 配对可沿用，必要时重新导入。
2. 在 Visual Studio 重新启动 NPEduTools Debug，使 App 和 Host 都使用新代码。无需应用 ZIP。
3. 打开「考试看板」，确认保存的程序位置正确、桥接已连接，找到 **考试方案 · 本机放映**。
4. 用 ExamAware 编辑器导出 UTF-8 JSON，或复制 [演示方案](../examples/examaware-plan-example.json) 并修改日期。点「选择并校验方案…」。确认摘要此时出现，放映尚未启动。
5. 点「开始放映」并确认。窗口出现与页面显示“已就绪”应分别成立；仅“已创建放映会话”不算就绪。
6. 再校验一份方案，已有放映时启动按钮应不可用。用 **ExamAware 放映页面自己的退出入口** 结束放映，观察“已关闭”。普通关闭窗口可能被 ExamAware 拦截。
7. 重启/停用桥接后，旧的已校验方案应失效，必须重新选择文件。

摘要对应**选择文件时读取的内容**，SHA-256 标识这份内容；之后在编辑器修改文件，需要重新选择。格式不正确、权限缺失、连接中断不会显示成成功，也不会自动重试播放。

## 只需读这四处代码

| 位置 | 职责 |
|---|---|
| `src/NPEduTools.App/ExamAwareWindow.Plans.cs` 和对应 XAML | 选文件、核对摘要、明确确认、呈现状态 |
| `src/NPEduTools.Contracts/ExamAwarePlans.cs` | 固定参数、大小与字段校验 |
| `src/NPEduTools.Host/ExamAwareService.Plans.cs` | 核对进程/配置版本、发送命令、去重、管理操作状态 |
| `plugins/npedutools-examaware-bridge/src/plans.ts` | 官方方案校验、内存缓存、禁止替换的放映调用、会话读取 |

传输沿用回环 TCP、双向挑战和 HMAC 序号；新增本地管道能力 `examaware.plan`，仅允许 `prepare/start`。已校验 ID 绑定当前桥接连接；启动时消费一次，后台保存请求收据防止重复受理。异步操作持有原有运行环境互斥租约，不能与 N3 切换重叠。考试正文和已校验内容不由 NPEduTools 写入配置/历史文件；ExamAware 自身按其正常机制处理。

保留每帧 64 KiB 上限：文件最多 24 KiB、32 场；名称最多 160 个 UTF-16 单元、提示语 2000、摘要文本合计 6000。编码后超限也拒绝。考试时间及配置格式由官方 `player.prepare` 校验。本次不提供覆盖、远程自动播放或播放终止命令。

沿用单次桥接激活 64 条命令上限。频繁调试达到上限会明确提示；在 ExamAware 中停用、重新启用桥接后重新校验即可。

## 已验证 / 待你确认

- Host、考试模式相关回归：133 项通过；插件回归：25 项通过。
- WPF 该页面在隔离检查项目中编译通过；未构建/替换你的应用 Debug/Release 输出。
- 隔离的 ExamAware **1.5.2 源码构建实例**：9 项通过，真实安装器、SDK、播放窗口均参与；验证非法方案拒绝、校验不播放、真实内容与 ready 读回、重复/已有放映拒绝、正常退出、重连失效、自启动零写入。
- 该实例来自本机已有源码构建副本，缺少 Git 元数据，报告记录入口 SHA-256；**不是正式安装版 EXE 的现场验收**。用户配置与自启动未改动。
- 最终实测记录：`.artifacts/examaware-real-host/2026-09-27T07-44-34-260Z/summary.json`，同目录 `exam-plan-player.png`。
- 仍需按上面步骤验收你安装的正式版，以及 NPEduTools 页面实际交互与长文本显示。

开发复验：`scripts/test-examaware-host.mjs --plans true` 使用隔离测试宿主，可用 `--host` 指定 `.artifacts` 中的宿主。插件 `npm test` 支持环境变量 `NPEEDUTOOLS_TEST_HOST` 指向隔离 Host。依赖路径、命令示例见插件 README。
