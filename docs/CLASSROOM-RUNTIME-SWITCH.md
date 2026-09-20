# 课堂模式：即时切换与全局状态显示

日期：2026-09-20。接续 [课堂模式第一版](CLASSROOM-MODES.md)。

## 已实现

主窗口“课堂模式”增加 **同时切换当前运行的软件** 开关，使用现有现代开关样式。每次启动 NPEduTools 默认关闭，不会因升级或上次选择而自动退出其他软件。同一管理窗口收起后重新打开会保留本次选择。

不启用此项时，保持原来的自启动设置与录课模式联动。为配置登录自启动，仍可能打开 ExamAware2，但不主动退出当前软件。

启用后，自启动设置核实成功，才执行以下即时操作：

| 目标 | 执行顺序 | 完成条件 |
| --- | --- | --- |
| 考试 | 确认 ExamAware2 桥接和目标进程就绪 → 正常退出 ClassIsland | 考试看板仍就绪，ClassIsland 已停止 |
| 日常 | 明确确认已保存并关闭考试编辑器、结束放映 → 正常退出 ExamAware2 → 通过管理员任务启动 ClassIsland | 考试软件进程全部退出，ClassIsland 具有管理员权限且课程接口响应成功 |

日常就绪检查不要求当天有课，也不要求某个科目正在上课；它检查程序接口能否响应。若普通权限 ClassIsland 已运行，管理员组件先请求其正常退出，再使用原管理员任务启动。此路径不回退到直接启动 EXE。

ClassIsland 的退出采用已有正常退出机制，核实进程路径、所属用户、会话和启动时间。拒绝退出、无响应、实例被替换或锁仍被占用时停止后续步骤。不会调用强制结束 ClassIsland。

ExamAware 复用 E3 的退出协议，等待准确目标进程退出，并短暂等待其子进程结束。桥接应答或连接断开本身不构成退出成功。返回日常的设置和重试都要求明确确认考试内容已处理完毕。

## 失败与重试

即时切换前保存目标、自启动核实快照、核实时刻和进度。正在切换时自动录课继续暂停；只有整体完成，日常模式才解除模式暂停。

失败后：

- 保留原模式名称并显示“切换未完成”，同时显示具体失败原因。
- 提供“重试即时切换”和原来的“恢复切换前设置”。
- 重试先重新核实配置和当前进程，不盲目重复上次操作。
- 如果考试软件已退出但 ClassIsland 启动失败，重试不会重新打开考试软件。
- 如果 ClassIsland 已启动但尚未就绪，重试会检查现有实例，不重复启动。
- 自启动设置或程序位置已经改变时停止即时重试，提示恢复或核实配置。
- 恢复入口只恢复自启动与录课模式，不自动撤销已经完成的软件启动／退出。

记录跨 Host 重启保留。重启不会自动继续退出软件，需要再次点击重试并确认。即时切换期间的正常后台退出会被拒绝，主窗口保持状态刷新；结果不确定的停止请求不会悄悄重新启动一个后台。

ExamAware 正常退出后无法继续通过桥接实时读取其自启动状态。页面显示的是退出前核实的登记及原核实时刻；后续在 Windows 中的外部修改仍需重新核实。

## 主窗口和侧边栏

- 主窗口概览新增模式状态卡，提供管理入口。
- 展开的侧边栏新增模式卡，点击可打开同一个管理窗口。
- 收起的侧边栏在品牌图标下显示“日常／考试／切换／待处理／未知”等短标签。
- 主窗口、侧边栏和模式管理页使用一致的模式名称与未完成提示。
- 考试暂停和切换失败的暂停都明确说明自动录课暂停、原计划保留。
- 后台连接丢失时清除可用模式显示，标为未知。
- 常规轮询只读取 Host 缓存，不轮询管理员组件或触发软件启动。

## 实现位置

- Contracts/ClassroomModes.cs：可选即时切换、考试保存确认、持久化运行意图。
- Host/ClassroomModeService.cs：即时阶段、显式重试、原恢复流程。
- Host/ClassroomRuntimeCoordinator.cs：运行软件操作顺序和失败中止。
- Host/ClassroomModeEffects.Runtime.cs：实际程序身份、退出、管理员任务启动和就绪核实。
- ClassIsland.Admin：新增受限 close / launch-mode 操作，复用任务指纹和进程核实。
- Host/ExamAwareService.cs：连接进程核实、退出请求的配置修订检查。
- App/ClassroomModePresentation.cs：主窗口与侧边栏共用的模式展示。
- App/ClassroomModeWindow、MainWindow、QuickAccessWindow：开关、确认、重试和模式状态。

## 验证

Release 构建零警告、零错误，锁定依赖还原通过。

**.NET：338 / 338 通过**。新增 19 个用例覆盖：

- 默认不执行即时操作；
- 两个方向的顺序与完成条件；
- 目标未就绪、退出拒绝、启动失败及最终核实失败时不推进后续步骤；
- 退出之后重试不重开考试软件、不重复启动已有 ClassIsland；
- 重试意图跨重启保留，启动时不自动重放；
- 恢复操作不继续退出软件；
- 目标、修订号和保存确认约束；
- 模式、未知、暂停、未完成的展示；
- ExamAware 连接进程的路径、修订和存活检查。

[测试报告](../.artifacts/classroom-runtime/runtime-modes.trx)

**真实 WPF 界面：13 项检查通过**：

- [默认状态与可选即时切换：3 项](../.artifacts/classroom-runtime-ui/71adeee3b3004275b211d4748f6a207f/summary.json)
- [考试状态与暂停提示：3 项](../.artifacts/classroom-runtime-ui/b9418645260e41bcb8d8c6932d82193f/summary.json)
- [未完成状态、恢复与取消重试：3 项](../.artifacts/classroom-runtime-ui/0770e30cd9bc4d2c94b4d70e2519c36d/summary.json)
- [原课堂模式入口回归：4 项](../.artifacts/classroom-ui/acf36ea4292a47c9985c79cf2c37a284/summary.json)

[考试模式侧边栏](../.artifacts/classroom-runtime-ui/b9418645260e41bcb8d8c6932d82193f/sidebar.png) · [未完成状态主页](../.artifacts/classroom-runtime-ui/0770e30cd9bc4d2c94b4d70e2519c36d/home.png)

界面测试在独立 Host 配置目录中预置展示状态并取消确认，没有改变本机真实自启动登记或关闭用户正在运行的软件。运行切换的顺序、拒绝和恢复使用隔离适配器验证；Windows 真实 UAC、两款正式软件完整往返及实际编辑器阻止退出仍需实机验收。本次不将这些未执行项目记作通过。

复现：scripts/test-classroom-runtime-ui.ps1 -Scenario Unconfigured / Exam / Incomplete；scripts/test-classroom-ui.ps1。开发与验证在 C 盘恢复工作区完成，最终按哈希核实回写 D 盘。

