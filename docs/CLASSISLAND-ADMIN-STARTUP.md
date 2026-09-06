# ClassIsland 管理员自启动与权限管理

日期：2026-09-06。状态：已实现界面、独立提权组件、任务兼容检查和可取消的正常退出流程。用户已实测 UAC 授权后创建任务成功，并反馈普通／管理员权限状态显示大体正常；剩余验收项目见下文。

## 使用

日常启动可直接使用首页或快捷面板的统一“启动”按钮，规则与实测见 [统一启动说明](CLASSISLAND-UNIFIED-LAUNCH.md)。本页面仍负责自启动配置和显式管理员操作。

首页 ClassIsland 一行的“自启动”进入管理区，也可在偏好设置中找到“ClassIsland 管理员自启动”。先选择并保存 ClassIsland 程序路径。

- **刷新**：普通权限查询任务与进程权限，不弹 UAC。
- **管理员检查**：在普通查询权限不足时，经过 UAC 再核实状态。
- **启用管理员自启动／更新并启用**：当前用户下次登录 Windows 时，以最高可用权限启动所选程序。
- **删除自启动任务**：删除同一任务，不关闭当前 ClassIsland，也不自动恢复普通自启动。
- **管理员启动／重启**：未运行时以管理员身份启动；普通权限运行时，授权后请求原实例正常退出再启动；已确认管理员运行时显示该状态并禁用重复重启。

NPEduTools 主窗口及 Host 继续以普通权限运行。Windows 的 UAC 授权仍由用户完成，取消授权不执行变更。要求使用当前登录用户的管理员凭据；不把其他账户的管理员实例冒充当前用户实例。

## 与 StartUpAsAdmin 的兼容

已核对本地插件 `classisland.startUpAsAdmin` 2.0.0.0 及[官方任务实现](https://github.com/ClassIsland/StartUpAsAdmin/blob/master/StartUpAsAdmin/ScheduledTaskHelper.cs)。复用根目录任务 `ClassIsland.AdminStartup`，不再创建平行任务，也不要求安装另一个 ClassIsland 插件。

定义使用当前用户登录触发、交互令牌、最高可用权限、所选程序及其工作目录，允许使用电池、不限制运行时长。创建成功后，核对并移除指向同一程序的普通自启动 `ClassIsland.lnk`，避免重复启动；其他路径的快捷方式保留并提示核对。不会修改用户其他任务。

同名任务必须同时符合当前用户、程序路径、工作目录、单个登录触发器及单个无额外参数的执行动作才可更新或删除。任务指向其他安装目录、其他用户或包含额外动作时显示冲突并保留。移动安装位置后遇到冲突，应核实原任务后再迁移，不自动覆盖。

提权前记录任务 XML 指纹，提权后重新读取并比较。等待授权期间任务发生变化则拒绝当前操作，要求刷新。任务不存在和访问被拒绝分别处理。

## 提权和重启边界

独立组件 `NPEduTools.ClassIsland.Admin.exe` 位于应用的 `Admin` 子目录，仅在需要时通过 `runas` 启动。双方使用随机、限当前用户访问的命名管道，并核对进程 ID；组件进一步验证发起进程的用户 SID、请求用户和会话。客户端只允许身份识别级模拟，避免将提权令牌借给普通权限进程。

.NET 的客户端 `CurrentUserOnly` 比较令牌 Owner，UAC 后它可能从用户变成 Administrators，因此组件端使用显式的进程及用户 SID 检查，服务端仍保留用户访问限制。依据：[.NET Windows 管道实现](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs)。

运行实例通过有限查询权限核对路径、用户 SID、会话、创建时间及提升状态。发现其他安装目录、其他用户／会话、多实例或身份不可读时停止重启。

普通权限实例重启时，先获得 UAC 授权，再向该进程的窗口发送带 `ENDSESSION_CLOSEAPP` 的可取消退出通知。任何窗口拒绝或无响应，向已询问窗口发送取消通知并结束操作。只有原实例退出、运行锁释放后才启动管理员实例。没有强制结束 ClassIsland 或关闭其进程树的回退。通知语义见 [WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession) 和 [WM_ENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession)。

组件操作有时限，异常或通信中断显示结果待核实，不自动重放。管理员启动的成功信息仅指进程及权限已确认；原有 Host 课程连接状态继续单独显示，不能将其等同于课程接口就绪。

## 本机验证

- Debug 构建无警告、无错误。
- 正常用户权限下完整回归 114 项通过，包括新增 11 项定义兼容、外部实体拒绝、账户、路径、动作和指纹检查。沙箱内第三方 IPC 测试受访问限制，完整结果以正常权限运行记录为准。
- Windows Task Scheduler 接受生成的 XML；此校验未注册任务。
- 初次只读检查确认插件已安装、ClassIsland 具有管理员权限，当时任务不存在；用户随后已成功创建任务。
- UAC 取消分支返回 `Cancelled`，未发送操作。用户已明确确认允许 UAC 后可以创建计划任务，允许分支的任务创建链路由用户实测通过。
- 专用图形测试进程验证生产代码的退出通知：同意退出时收到通知并关闭；拒绝时保留进程。结果位于 `.artifacts/admin-restart-test/result.txt`。
- 窗口回归记录位于 `.artifacts/app-smoke/3a15bb5c50ae47fdbbe99c2da28763f4/`。

### 用户实机反馈

用户提供四张截图并反馈“大体是正常的”：

- NPEduTools 显示任务已启用、ClassIsland 当前以普通权限运行；此时插件提示需管理员权限才能修改设置。
- 后续插件管理按钮可用；NPEduTools 同时显示 ClassIsland 以管理员身份运行，并禁用重复重启按钮。
- 两组界面状态一致，任务是否启用与当前进程是否提权被正确区分。

截图能够确认上述前后状态，但没有记录重启按钮来自 NPEduTools 还是插件，因此不单凭截图断言 NPEduTools 的整个重启及课程重连流程均已验收。任务更新、删除后当前实例继续运行、重新登录后实际自启动，以及重启后的课程连接恢复仍保留在后续检查清单。用户实测与代理此前的只读检查、独立测试进程验证分别记录，不混称为代理执行。目标大屏物理触摸试用仍按原安排暂缓。
