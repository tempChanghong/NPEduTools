# 课堂模式真实环境验收：第一轮

日期：2026-09-20。结果：**已完成本机前置检查和缺少配置时的保护验证；完整模式往返尚未验收通过。**

## 环境

直接启动 D 盘现有 Release 程序，使用当前用户的真实配置目录，不预置模拟课堂模式。程序路径为 `D:\WebstormProjects\NPEduTools\src\NPEduTools.App\bin\Release\net10.0-windows\NPEduTools.App.exe`。

- ClassIsland 已保存的路径是开发构建 `D:\WebstormProjects\ClassIsland\ClassIsland.Desktop\bin\Debug\net8.0-windows10.0.19041.0\ClassIsland.Desktop.exe`，文件存在；本轮未启动它，不代表已验证该构建的实际运行。
- 当前用户环境没有 `ClassIsland.AdminStartup` 任务。Windows 任务查询及产品自己的状态核实均证实任务缺失。
- `D:\WebstormProjects\ExamAware2` 是源码目录。在已检查的源码、下载、安装登记等位置尚未定位到正式 `ExamAware.exe`；不据此断言全盘没有安装。
- 初始配置目录没有 ExamAware 配对配置，也没有课堂模式记录；两款目标软件未运行。

## 已执行结果

| 项目 | 结果与证据 |
| --- | --- |
| 启动实际 Release 程序 | 成功进入主页；模式显示“尚未设置模式”，ClassIsland 显示暂不可用 |
| 主页模式管理入口 | 成功打开课堂模式窗口 |
| 即时切换默认值 | 实际开关显示关闭；可以开启，测试后恢复关闭 |
| 只读核实 | 明确报告管理员任务不可用，没有启动目标软件 |
| 即时切换确认 | 开启开关并选择考试模式，确认框明确说明先准备 ExamAware2，再正常退出 ClassIsland |
| 缺少任务时提交切换 | 实际确认后终止于前置检查，显示“检查未通过，未修改自启动设置” |
| 失败后的真实状态 | 持久记录为 `Unconfigured / Idle`，`automaticPaused=false`，`actual/recovery/runtime=null`，没有错误标记成功或进入待恢复阶段 |
| 失败后主页显示 | 仍显示尚未设置模式 |
| 正常退出 | 从产品退出入口关闭，本次 App 和 Host 均已退出 |
| 测试后配置核对 | ClassIsland 配置 SHA-256 前后一致；管理员任务仍不存在；已检查的 HKCU/HKLM Run 中没有相关登记；目标软件未运行 |

点击“展开侧边栏”后主窗口收起，但桌面工具没有列出可供观察的快捷工具窗口，因此侧边栏实际显示暂记为**未验证**，不能据此判定产品故障。再次启动程序能够唤回同一个主窗口。

## 尚待执行

1. 确定本机实际使用的 ExamAware2 正式程序，配置程序位置、安装兼容桥接并完成配对。
2. 为实际使用的 ClassIsland 路径创建管理员自启动任务，并记录原始开启状态。
3. 完成日常 → 考试 → 日常真实往返，核实目标就绪、正常退出、管理员启动及最终进程身份。
4. 实测编辑器或放映阻止退出、处理后重试，以及部分完成后的恢复。
5. 验证考试期间自动录课暂停，返回日常后原计划和原启用状态保留。
6. 补验侧边栏两种模式显示；实际登录启动另行安排，不在工作中的电脑上自动重启或注销。

前一轮的 338 项自动测试和 13 项隔离界面检查不计作本轮真实联动通过。

## 留存与清理

本轮没有修改源代码、Windows 自启动设置或两款目标软件配置；仅由产品生成课堂模式检查记录，并可能更新普通窗口偏好。检查记录保留供诊断，不人为改成成功状态。

现场证据保存在 `C:\Users\Changhong\NPEduTools-live-acceptance-20260920`：`config-before`、`mode-preflight-ui.txt`、`classroom-mode-after.json` 和 `after-check.json`。原有 ClassIsland 配置校验值为 `5138A130AFD01488B3A164D11421C294FE40A94D0375484693E8F3B39BB1302F`。
