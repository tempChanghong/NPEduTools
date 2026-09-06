# PowerPoint 触摸诊断原型

默认观察与日志分析保持只读，尚未启用触摸自动翻页。工具另提供显式“单次推进实验”入口，会改变当前放映位置，仅面向已验证的简单测试文稿。用户的目标环境是 Office 2024 触摸大屏，当前开发机与目标机不同。

## 在目标大屏运行

1. 将 Windows x64 诊断 ZIP 完整解压到可写文件夹。包内自带 .NET 运行时，无需安装 SDK，也无需 ClassIsland 或 Python。
2. 双击 `Start-Diagnostics.cmd`。工具最多观察 180 秒，按 Ctrl+C 可提前结束。它不会自动打开 PowerPoint。
3. 在 Microsoft PowerPoint 中打开 `diagnostic-slides.pptx` 并进入全屏放映。新版文稿有五页：逐步动画、普通页、内部链接、形状触发器、菜单/书写/手势测试区。
4. 分别尝试手指轻点、鼠标点击、轻微抖动、拖动后回到原处、长按及右键菜单；第三页的按钮应跳回第二页，第四页的 Reveal 按钮应在原页显示答案。第五页用于手动测试菜单与书写；文稿本身不自动识别这些操作。媒体播放另用专门测试文稿验证。
5. 退出放映，等待诊断结束。记录位于 `app/diagnostics/*.jsonl`，旁边自动生成中文 `*.analysis-*.md` 报告。两者都可复制回开发机；另记下实际触摸行为与双屏模式等条件。

只读观察不会修复 PowerPoint 原有触摸行为。如果手指轻点本来就不翻页，本阶段仍可能不翻页。

## 记录内容与限制

- 环境：Windows、进程架构、.NET、系统声明的触摸能力；已连接 PowerPoint 时记录 COM 版本及可读取的 EXE 构建号。COM 的 `16.0` 本身不能证明是 Office 2024。
- 放映：窗口边界、DPI、进程身份、匿名文稿关联 ID、幻灯片 ID/序号、动画点击序号/数量、指针和放映状态。只连接已经运行的 PowerPoint，不启动或保存文稿。
- 页面清单：记录当前页的形状总数、顶层动作/媒体/组合形状计数、链接数及交互动画序列数，不读取链接地址、动作命令或文本。每页最多检查 64 个顶层形状；结果按页缓存最多约 3 秒，附有采样时间及覆盖范围。母版、组合内部及点击位置不在清单范围内，零计数不能用来认定安全背景。
- 输入：只记录命中当前前台放映窗口的兼容鼠标按下/移动/松开/右键事件，以及输入来源标记。手势移出目标时记录不含外部坐标的取消事件。不采集键盘内容、窗口标题、文稿路径或正文。
- 输出采用逐行 JSON；包括 `environment`、`show`、`input`、`gesture`、`inputGap` 和 `summary`。单次默认 120 秒，最多 1800 秒；文件达到约 32 MiB 后停止（检查按批次执行，可能略超出）。文件使用新建模式，不覆盖已有结果。
- `TouchMarked` 表示符合 Windows 触摸兼容鼠标标记；`MouseOrUnmarked` 不能证明来自真实鼠标。`Injected` 单独报告，原始标记和 flags 仍在记录中。
- `TouchTapCandidate` 只说明记录的输入符合实验阈值：450 ms 内、整个轨迹最大位移不超过 12 DIP、目标没有变化。它不说明该处没有菜单、视频或链接，也不证明 PowerPoint 尚未处理该触摸。
- 观察约每 250 ms 读取一次 COM 状态；输入只关联接收后 750 ms 内的快照。采样存在延迟，不足以直接证明某次点击与某次动画的因果关系，也不保证完整捕获自动动画。
- 当前窗口映射只支持单 PowerPoint 进程、单放映窗口，通过 COM 放映集合与该进程唯一可见的 `screenClass` 原生窗口关联。多实例、多放映和不能唯一映射时报告不支持；窗口类是本机已验证的实现细节，仍需在 Office 2024 目标机验证。
- 可读取 PowerPoint 的指针类型和有限页面交互清单，但没有完整多指流、菜单检测或交互对象命中识别；没有输入拦截、透明覆盖层或自动翻页。双屏演讲者视图尚未验收，非前台放映画面不收集输入。
- 低级钩子有系统超时及驱动兼容限制。没有事件不等于没有触摸；只有状态正常也不等于钩子在所有设备上持续有效。队列溢出/回调异常计入结果，并取消不完整手势；会话结束时队列尾部可能省略。

工作进程在专用 STA 中读取 COM。5 秒无响应时父进程清理自有工作进程并报告超时；父进程退出导致 stdin 关闭时工作进程退出，租约超过 8 秒也会自行退出。不会终止 PowerPoint。工具是单独运行的原型，尚未接入 NPEduTools 主窗口开关。

## 单次推进实验（会改变放映）

明确需要测试推进时，运行 `EXPERIMENT-Step-Once.cmd`，或开发机的 `./scripts/step-powerpoint-experiment.ps1`。每次运行发起一个新的请求；没有接入全局鼠标或触摸监听，观察模式不会调用此入口。

先使用附带测试文稿，在 PowerPoint 全屏放映中选择箭头指针。第一页第一次调用应显示按次动画，第二次调用应进入第二页。第二页继续调用会拒绝，因为下一页包含交互链接。笔模式会拒绝；最后一页返回 `NoOp/EndOfShow`，不会退出放映。

当前支持范围有意限定为：单进程、单全屏放映、箭头或自动箭头、正常放映状态、普通全范围且不循环；页面最多 64 个顶层普通形状/文本框；只有即时、无延迟的 Appear 动画且每次点击对应一个效果。已知链接、动作、触发器、媒体、组合、隐藏页、自动换页、过渡效果和其他动画拒绝执行。此范围面向实验文稿，不表示完整覆盖母版、布局或复杂文稿的行为。

执行前读取实际文稿/窗口/页码/动画状态，规划调用一次 `GotoClick` 或 `Next`，再次复核状态后执行并回读。PowerPoint 的读取与修改没有跨进程原子事务；用户或其他程序同时操作仍可能改变结果。因此“预期状态已观察到”不能推广为真实触摸恰好执行一次的保证。[GotoClick](https://learn.microsoft.com/en-us/office/vba/api/powerpoint.slideshowview.gotoclick)、[Next](https://learn.microsoft.com/en-us/office/vba/api/powerpoint.slideshowview.next)

同一会话中的实验请求串行互斥，忙时直接拒绝。父进程最多等待 7 秒，工作进程另有 8 秒硬期限；超时只清理自有工作进程，结果标为 `Unknown`，不自动重试或回滚。PowerPoint 内已经接收的 COM 调用可能继续完成，因此应核对放映位置。

结果为 `Succeeded`、`NoOp`、`Refused` 或 `Unknown`，分别表示观察到预期状态、无需操作、未发出操作或无法确认。意图先写入并刷新到 `app/diagnostics/step-*.step.jsonl`，随后保存结果；这些记录不用于自动重放，缺少结果的记录保持不确定。步进记录与观察日志格式不同，不使用 `Analyze-Log.cmd` 分析。退出码为 0（成功/无需操作）、3（拒绝）、4（不确定），执行器启动或记录错误为 1。

## 中文分析报告

观察正常结束后自动生成报告。已有日志也可拖到 `Analyze-Log.cmd` 上重新分析；此模式不连接 Office、不安装输入钩子。每次报告使用新文件名，不修改或覆盖原始日志。

报告汇总输入来源、轻点候选、放映状态和已观察到的交互对象，并检查开始/结束记录、序号、时间、坏行、丢失和容量限制。支持上一版不含页面清单的日志；文件上限 40 MiB，单行上限 64 KiB。

“日志结构完整”只表示检查过的结构条件成立。相邻同一目标、间隔不超过 750 ms 的快照用于统计动画序号或幻灯片变化；重连、窗口切换和采样中断不跨段推算。这些变化可能由鼠标、键盘、自动动画等引起，不能据此确定触摸是否被原生处理，更不会自动开启辅助翻页。

## 开发机命令

从仓库根目录执行：

```powershell
# 单次只读查询；不安装输入钩子。
./scripts/start-powerpoint-diagnostics.ps1 -Probe

# 观察两分钟；项目构建后的运行时为 .NET 10。
./scripts/start-powerpoint-diagnostics.ps1 -Seconds 120

# 自定义输出；路径已存在时拒绝覆盖。
./scripts/start-powerpoint-diagnostics.ps1 -Seconds 60 -OutputPath .artifacts/ppt-trace.jsonl

# 离线分析已有日志，兼容上一版日志。
./scripts/start-powerpoint-diagnostics.ps1 -Analyze .artifacts/ppt-trace.jsonl

# 独立真实 Office 测试：已有 PowerPoint 运行时拒绝执行。
./scripts/test-powerpoint-diagnostics.ps1

# 显式改变当前放映的实验命令（简单文稿）。
./scripts/step-powerpoint-experiment.ps1

# 在独立测试文稿中验证动画→换页、拒绝交互页/笔模式和末页保持。
./scripts/test-powerpoint-diagnostics.ps1 -StepExperiment

# 生成包含运行时的 ZIP；可选附带测试生成的 PPTX。
./scripts/package-powerpoint-diagnostics.ps1 -TestPresentation <测试文稿的实际路径>
```

真实 Office 测试通过 COM 创建独立的五页测试文稿，检查未运行/未放映/放映、动画点击序号变化、换页、内部链接/动作及触发器清单、笔指针状态、退出与自动报告生成；结束时只关闭测试文稿，没有其他文稿时退出本次 Office 应用。不会发送模拟鼠标或触摸，不将创建菜单测试页当作已经通过菜单触控测试。测试资料保存在 `.artifacts/powerpoint-live/`。

只读查询退出码：0 表示成功读到正在放映、未放映或未运行；3 表示 COM 不可达、映射不支持、超时等；2 表示参数错误；1 表示启动或输出失败。观察模式可以记录上游不可用状态后正常退出，退出码 0 不表示触摸兼容验收通过。

## 验证记录（2026-09-06）

开发机 PowerPoint 文件版本为 `16.0.20326.20132`。真实 Office 测试已验证初始页 `ClickIndex=0/ClickCount=1`，动画后仍在第一页且 `ClickIndex=1`，随后显示第二页，并能读取退出放映后的状态。测试中检测到 2560×1600 放映区域及 144 DPI。

全解决方案 Debug 构建通过，0 警告、0 错误；桌面用户会话下完整测试 58 项通过，包括新增 19 项输入识别与工作进程测试。原有 Release App 正在运行并锁定程序集，因此全解决方案验证使用独立 Debug 输出；诊断工具单独以 Release 构建和发布。沙箱中的旧 IPC 集成测试因管道访问限制失败，在正常桌面会话中复测全部通过；未修改既有 IPC 逻辑。

本次扩展：PowerPoint 专项测试 36 项通过（原有 19 项，加 17 项日志分析与报告测试），覆盖旧日志、坏行/截断、丢失、身份/采样中断、输入标记、只读文件分析和报告不覆盖。真实 Office 扩展联调已验证：第三页有 1 个动作形状和 1 个内部链接，第四页有 1 个触发器序列，第五页可读到笔指针状态，观察结束自动生成中文报告。相关实现依据为微软的 [ActionSetting.Action](https://learn.microsoft.com/en-us/office/vba/api/powerpoint.actionsetting.action) 与 [InteractiveSequences](https://learn.microsoft.com/en-us/office/vba/api/powerpoint.timeline.interactivesequences) 文档。

扩展后完整回归共 75 项通过，0 跳过；Debug 全解决方案构建为 0 警告、0 错误。源码启动脚本也修正了构建命令的参数：先执行锁定还原，再执行不还原的构建。

单次推进实验新增 12 项规划/状态/结果测试，PowerPoint 专项共 48 项通过。真实 Office 实验已验证动画推进、换页、拒绝链接/触发器页与笔模式、最后一页保持放映。尚未进行目标大屏的自动触摸触发验收。

加入执行侧原型后完整回归共 87 项通过，0 跳过；真实 Office 执行实验记录在 `.artifacts/powerpoint-live/1a1d57a57d3649fa807fac355ec9604c/summary.json`。未运行 PowerPoint 时，单次推进返回 `Refused/NotRunning`，不会启动 Office。

目标 Office 2024 触摸大屏的轻点、重复推进、菜单/书写及多显示器行为尚未验收。当前交付只用于取得这些验证所需的诊断证据，后续实施关卡见 [专项设计](POWERPOINT-TOUCH-ASSIST-PLAN.md)。
