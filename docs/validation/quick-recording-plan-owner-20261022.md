# 2026-10-22：快捷栏打开自动录课计划导致界面进程退出

源码起点：`codex/study-microphone-recovery-20261020`，`c394914ef561b858146a6a3d564a337a404f18cb`；开始时工作区干净。本轮仅修改 NPEduTools，未更改版本号。

## 现场与复现

- 用户提供的 2026-10-22 07:23:09 Windows Application / .NET Runtime 1026 记录确认：`NPEduTools.App.exe` 因未处理的 `InvalidOperationException` 退出，提示“无法将 Owner 属性设置为之前未显示的 Window”。调用链为 `QuickAccessWindow.RecordingPlanClicked → MainWindow.ShowRecordingPlan → ShowRecordingWorkspace → WorkspacePanel`。
- 新增隔离 WPF 检查，使用从未显示的真实 MainWindow、真实快捷栏按钮、真实内嵌录课页面和私有管道。修改业务源码前，同一路径复现同一异常，证据：`.artifacts/quick-plan-route/result.json`。主窗口内进入的原有检查先显示过 MainWindow，未覆盖静默启动后的首次快捷入口。
- 静默登录只显示快捷栏时，MainWindow 尚未创建原生窗口源；WorkspacePanel 在主窗口恢复之前设置控制器 Owner，WPF 拒绝该操作。此异常来自页面初始化，不是录制引擎错误；没有据此推断现场 Host 的录制是否中断。

## 修复

WorkspacePanel 在设置 Owner 前调用 `WindowInteropHelper.EnsureHandle()`，为隐藏主窗口建立原生窗口源。该调用不显示窗口、不启停录制或监测。修复放在共用内嵌页面路径，自习、校准和手动录课等同类工作区同步使用；保留原窗口归属、弹窗归属及页面控制器复用。

## 验证

- 录课工作区 WPF：9 项通过，`.artifacts/quick-plan-final/result.json`。新增 5 项：从未显示的 Owner 不闪现且只创建一次窗口源、静默启动首次打开、已显示后收起、最小化、反复在记录和计划间切换。实际快捷栏按钮恢复主窗口，重复打开复用控制器，未处理的 Dispatcher 异常为零，导航未发送录制修改命令。
- 自习工作区 WPF：38 项通过，`.artifacts/quick-plan-study-regression/result.json`，覆盖手动预约、切页、学校页、管理返回、报告和隔离联机校准流程。
- 快捷栏 WPF：6 项通过，`.artifacts/quick-plan-sidebar-regression/result.json`，包含点击／拖动边界、触控相关输入路径和状态变化检查。不能代替真实一体机触控验收。
- Release App 编译：0 警告、0 错误，`.artifacts/quick-plan-release-build.log`。

均为隔离 WPF、合成录制状态和私有管道验证，未打开真实麦克风、摄像头、USB、录制设备或学校服务。现场崩溃日志已与复现调用链对应，修复版本尚未在故障一体机安装验收。保持本地提交，未推送、部署或发版。
