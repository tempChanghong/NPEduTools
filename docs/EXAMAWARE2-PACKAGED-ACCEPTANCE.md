# ExamAware2 本机构建与打包程序验收

日期：2026-09-20。状态：**Windows x64 打包、生产桥接、正常退出、重启重连、真实自启动登记开关及日常 → 考试 → 日常往返通过。按用户选择，测试后已恢复两款软件自启动均关闭。**

## 交付

使用用户提供的 `D:\WebstormProjects\ExamAware2` 源码，提交 `7979213fed918eaece7a5bf424e15f534778d7f2`，Desktop 版本 1.5.2，Electron 39.2.7。

可运行程序：

`D:\WebstormProjects\ExamAware2\packages\desktop\out\win-unpacked\ExamAware.exe`

这是从上述源码本机构建的完整 Windows x64 目录版，未生成或运行安装向导。运行时需要保留整个 `win-unpacked` 文件夹，不能只复制 EXE。它不是官方签名发行包，签名状态为 `NotSigned`。

EXE SHA-256：`DD782D2B961076DFA900EA46051A1E198A37E9E1232871464DD5162E5D918E5C`。

## 构建与校验

1. 复制源码到 `C:\Users\Changhong\ExamAware2-build-20260920`，排除 Git、依赖目录和旧打包输出；D 盘源码未修改。
2. 使用 Node 24.14.1、仓库指定 pnpm 10.18.2、锁文件安装依赖；构建 rpc、core、player、plugin-sdk、control-protocol 和 desktop。
3. 执行上游 `prepare:package`，生产依赖闭包校验通过：212 个依赖包。
4. 使用 electron-builder 26.0.12 生成 Windows x64 目录包；打包后的 app.asar 再次通过 212 个依赖包校验。
5. 打包工具 winCodeSign 2.6.0 首次解压因无权创建 macOS 符号链接失败。将同一工具压缩包中本次不需要的 darwin 目录排除后，解压到 C 盘独立工具缓存，重新打包成功；没有更改系统权限设置。
6. 产品名 `ExamAware`、文件版本 `1.5.2`、app.asar 存在检查通过。76 个产物文件逐一校验后复制到 D 盘目标目录。

构建有上游大文件分块、Node 弃用和依赖 peer 提示；最终打包退出码为 0。未修改上游源码来掩盖提示。

## 已执行的真实操作

- 启动现有 NPEduTools Release，使用当前用户实际配置。
- 通过生产 Host 保存上述 ExamAware.exe 路径，生产路径校验通过；未放宽产品适配限制。
- 通过生产 `examaware.start` 发起启动，实际出现 ExamAware 主页面；核对到真实目标路径下的主进程和子进程。
- 通过生产 `examaware.plugins` 打开官方插件设置，实际插件列表初始为空。
- 从本地官方安装入口选择 `npedutools-examaware-bridge-0.3.0.ea2x`，用户亲自完成权限确认与安装。
- 导出本机配对文件用于下一步导入；配对密钥没有写入本报告或公开测试记录。

## 安装后的真实联动验收

用户确认安装后，通过 ExamAware 主页面“连接 NPEduTools”导入配对文件。生产 Host 读回 `Connected`、版本 `1.5.2`、`packaged=true`、`canSetAutoStart=true`，初始登录自启动为关闭。

| 项目 | 结果 |
| --- | --- |
| 正式打包程序与生产桥接连接 | 通过，没有采用源码测试宿主或放宽路径校验 |
| 开启真实自启动 | 官方插件 API 及独立读回成功，Windows 原生查询确认 HKCU Run 的 `org.examaware` 值准确指向已保存的 ExamAware.exe |
| 恢复自启动关闭 | 官方读回 false，Windows 原生查询确认上述登记已移除 |
| 正常退出 | 主页面和设置页面打开、无编辑器及放映窗口；Host 状态为 Exited，随后独立进程查询确认 ExamAware 进程数为零；没有强制终止 |
| 再次启动与配对持久化 | 生产启动入口重新启动后，桥接自动连接，不需重新导入，登录自启动仍为关闭 |

自启动独立检查最初使用本机 Microsoft Store Python 的注册表读取，未观察到实际登记，三次探测均在 finally 中恢复关闭。这些失败探测不计通过。随后改用 Windows 原生 PowerShell 查询，确认实际登记项及删除结果，与官方桥接读回一致；没有因此修改产品代码。此轮使用真实 Windows 登记，没有替换原生自启动接口。

## 课堂模式真实往返

后续异常场景已单独实测，见 [课堂模式异常实机验收](CLASSROOM-EXCEPTION-ACCEPTANCE.md)。取消授权、断线保护、接口未就绪及显式恢复通过；编辑器未关闭时的上游退出问题已在此打包程序中复现，不能将正常往返的通过外推为编辑器内容可安全保存。

用户在系统 UAC 窗口中授权，产品成功创建 `ClassIsland.AdminStartup`；任务指向已保存的 ClassIsland 开发构建。先启动并确认课程 IPC 响应，再以日常模式为基线执行往返。请求使用生产 Host 的 `classroom.set` 和真实配置，勾选等效参数 `switchRunning=true`；没有采用隔离测试宿主或替换自启动接口。本轮不是对模式窗口每个按钮的重复 UI 验收。

| 阶段 | 产品结果 | 独立核对 |
| --- | --- | --- |
| 日常基线 | `Daily / Idle`，revision 7，`automaticPaused=false` | ClassIsland 管理员任务启用，ExamAware 自启动关闭 |
| 日常 → 考试 | `Exam / Idle`，revision 14，`matchesMode=true`，`automaticPaused=true` | ClassIsland 进程数为 0、管理员任务禁用；ExamAware 桥接 Connected；Windows Run 登记准确指向本次打包 EXE |
| 考试 → 日常 | `Daily / Idle`，revision 21，`matchesMode=true`，`automaticPaused=false`，无待恢复操作 | ExamAware 进程数为 0，Run 登记不存在；ClassIsland 任务启用且 RunLevel=Highest，存在 1 个进程，课程 IPC 成功响应 |

产品在返回日常时实际执行管理员实例身份核实，成功后才提交模式；独立普通权限 CIM 查询能够看到 ClassIsland PID，但其 ExecutablePath 为 null，因此不能将该查询单独当作管理员身份或路径证明。任务动作的程序路径另行读回，与保存路径一致。

两款软件均通过正常退出流程关闭，没有强制结束目标进程。返回日常前，实际窗口列表只有 ExamAware 主窗口，没有编辑器或放映窗口。

本轮确认考试模式暂停标记开启、日常模式解除；现场 ClassIsland 未加载课表，因此**不将本轮算作真实课程触发、录制收尾或恢复录制的验收**，也没有覆盖已有录制计划的运行行为。未执行实际注销登录、任务管理器禁用开关、未保存编辑器阻止退出、放映阻止退出及失败恢复分支；这些仍需后续专项测试。

## 配置影响与待办

NPEduTools 已保存新程序路径并完成桥接配对。ExamAware 启动时按上游逻辑登记了 `examaware://` 协议，指向当前打包目录。用户明确选择“恢复测试前：移除新建管理员任务，两者自启动关闭”，清理已完成：

- 通过现有管理员组件正常退出 ClassIsland，再删除此次创建的任务；独立任务查询结果为 0。
- ExamAware 已在返回日常过程中正常退出，独立进程查询及 Windows Run 查询均确认无残留进程、无自启动登记。
- 从 NPEduTools 的正常退出入口关闭 App 和 Host，确认均退出后，将本次备份的 `classroom-mode.json` 原样恢复；SHA-256 与备份一致。因此再次打开时仍是测试前的 `Unconfigured`，不能将测试的成功记录误认为当前仍配置了日常自启动。
- 原有 `classisland.json` 与测试前备份 SHA-256 一致。保留新配置的 ExamAware 路径、桥接插件与配对，以及本机构建程序；没有注销或重启电脑。

原 NPEduTools 配置、自启动登记快照以及已存在的 ExamAware 配置备份留在 C 盘构建目录的 `acceptance` 子目录。配对导入成功后清理本次导出的临时配对文件，软件内部配对保持有效。

以后正式启用课堂模式时，需要重新创建 ClassIsland 管理员任务，再选择日常或考试模式。编辑器与放映的正常退出限制仍见 [E3 报告](EXAMAWARE2-STAGE3.md)。本轮没有修改产品代码。

构建摘要、76 文件校验清单、打包日志、`connected-before.json`、`startup-windows-enabled.json`、`startup-windows-restored.json`、`quit-live.json`、`restart-connected.json`、`mode-daily-before.json`、`exam-mode-live.json`、`daily-mode-live.json`、`cleanup-admin.json`、`cleanup-verified.json`：`.artifacts/examaware-packaged/20260920/`。这些记录不含配对文件或密钥；新增往返及清理证据已从 C 盘复制到 D 盘并逐个核对 SHA-256。
