# PowerPoint 触摸诊断原型

这是只读诊断工具，尚未启用单击辅助翻页。适用于验证 Microsoft PowerPoint 放映及触摸兼容性；用户的目标环境是 Office 2024 触摸大屏，当前开发机与目标机不同。

## 在目标大屏运行

1. 将 Windows x64 诊断 ZIP 完整解压到可写文件夹。包内自带 .NET 运行时，无需安装 SDK，也无需 ClassIsland 或 Python。
2. 双击 `Start-Diagnostics.cmd`。工具最多观察 180 秒，按 Ctrl+C 可提前结束。它不会自动打开 PowerPoint。
3. 在 Microsoft PowerPoint 中打开测试文稿并进入全屏放映。若包中有 `diagnostic-slides.pptx`，可先用它验证；第一页有一次点击动画，第二页没有动画。
4. 分别尝试手指轻点、鼠标点击、轻微抖动、拖动后回到原处、长按及右键菜单。再使用专门准备的文稿测试书写、超链接、视频和触发器动画。
5. 退出放映，等待诊断结束。记录位于 `app/diagnostics/*.jsonl`，可复制回开发机分析。另记下哪些手势推进了动画/幻灯片、哪些没有，以及双屏模式等测试条件。

只读观察不会修复 PowerPoint 原有触摸行为。如果手指轻点本来就不翻页，本阶段仍可能不翻页。

## 记录内容与限制

- 环境：Windows、进程架构、.NET、系统声明的触摸能力；已连接 PowerPoint 时记录 COM 版本及可读取的 EXE 构建号。COM 的 `16.0` 本身不能证明是 Office 2024。
- 放映：窗口边界、DPI、进程身份、匿名文稿关联 ID、幻灯片 ID/序号、动画点击序号/数量、指针和放映状态。只连接已经运行的 PowerPoint，不启动或保存文稿。
- 输入：只记录命中当前前台放映窗口的兼容鼠标按下/移动/松开/右键事件，以及输入来源标记。手势移出目标时记录不含外部坐标的取消事件。不采集键盘内容、窗口标题、文稿路径或正文。
- 输出采用逐行 JSON；包括 `environment`、`show`、`input`、`gesture`、`inputGap` 和 `summary`。单次默认 120 秒，最多 1800 秒；文件达到约 32 MiB 后停止（检查按批次执行，可能略超出）。文件使用新建模式，不覆盖已有结果。
- `TouchMarked` 表示符合 Windows 触摸兼容鼠标标记；`MouseOrUnmarked` 不能证明来自真实鼠标。`Injected` 单独报告，原始标记和 flags 仍在记录中。
- `TouchTapCandidate` 只说明记录的输入符合实验阈值：450 ms 内、整个轨迹最大位移不超过 12 DIP、目标没有变化。它不说明该处没有菜单、视频或链接，也不证明 PowerPoint 尚未处理该触摸。
- 观察约每 250 ms 读取一次 COM 状态；输入只关联接收后 750 ms 内的快照。采样存在延迟，不足以直接证明某次点击与某次动画的因果关系，也不保证完整捕获自动动画。
- 当前窗口映射只支持单 PowerPoint 进程、单放映窗口，通过 COM 放映集合与该进程唯一可见的 `screenClass` 原生窗口关联。多实例、多放映和不能唯一映射时报告不支持；窗口类是本机已验证的实现细节，仍需在 Office 2024 目标机验证。
- 没有完整多指流、笔迹模式/菜单/交互对象识别；没有输入拦截、透明覆盖层或自动翻页。双屏演讲者视图尚未验收，非前台放映画面不收集输入。
- 低级钩子有系统超时及驱动兼容限制。没有事件不等于没有触摸；只有状态正常也不等于钩子在所有设备上持续有效。队列溢出/回调异常计入结果，并取消不完整手势；会话结束时队列尾部可能省略。

工作进程在专用 STA 中读取 COM。5 秒无响应时父进程清理自有工作进程并报告超时；父进程退出导致 stdin 关闭时工作进程退出，租约超过 8 秒也会自行退出。不会终止 PowerPoint。工具是单独运行的原型，尚未接入 NPEduTools 主窗口开关。

## 开发机命令

从仓库根目录执行：

```powershell
# 单次只读查询；不安装输入钩子。
./scripts/start-powerpoint-diagnostics.ps1 -Probe

# 观察两分钟；项目构建后的运行时为 .NET 10。
./scripts/start-powerpoint-diagnostics.ps1 -Seconds 120

# 自定义输出；路径已存在时拒绝覆盖。
./scripts/start-powerpoint-diagnostics.ps1 -Seconds 60 -OutputPath .artifacts/ppt-trace.jsonl

# 独立真实 Office 测试：已有 PowerPoint 运行时拒绝执行。
./scripts/test-powerpoint-diagnostics.ps1

# 生成包含运行时的 ZIP；可选附带测试生成的 PPTX。
./scripts/package-powerpoint-diagnostics.ps1 -TestPresentation <测试文稿的实际路径>
```

真实 Office 测试通过 COM 创建独立的两页测试文稿，检查未运行/未放映/放映、动画点击序号变化、换页、退出及观察文件的结束记录；结束时只关闭测试文稿，没有其他文稿时退出本次 Office 应用。不会发送模拟鼠标或触摸。测试资料保存在 `.artifacts/powerpoint-live/`。

只读查询退出码：0 表示成功读到正在放映、未放映或未运行；3 表示 COM 不可达、映射不支持、超时等；2 表示参数错误；1 表示启动或输出失败。观察模式可以记录上游不可用状态后正常退出，退出码 0 不表示触摸兼容验收通过。

## 验证记录（2026-09-06）

开发机 PowerPoint 文件版本为 `16.0.20326.20132`。真实 Office 测试已验证初始页 `ClickIndex=0/ClickCount=1`，动画后仍在第一页且 `ClickIndex=1`，随后显示第二页，并能读取退出放映后的状态。测试中检测到 2560×1600 放映区域及 144 DPI。

全解决方案 Debug 构建通过，0 警告、0 错误；桌面用户会话下完整测试 58 项通过，包括新增 19 项输入识别与工作进程测试。原有 Release App 正在运行并锁定程序集，因此全解决方案验证使用独立 Debug 输出；诊断工具单独以 Release 构建和发布。沙箱中的旧 IPC 集成测试因管道访问限制失败，在正常桌面会话中复测全部通过；未修改既有 IPC 逻辑。

目标 Office 2024 触摸大屏的轻点、重复推进、菜单/书写及多显示器行为尚未验收。当前交付只用于取得这些验证所需的诊断证据，后续实施关卡见 [专项设计](POWERPOINT-TOUCH-ASSIST-PLAN.md)。
