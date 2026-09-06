# PowerPoint 触摸翻页

独立 Windows 小工具，目标是实现参考项目 **PowerPoint-Touch-Assist** 的实际效果：在 Microsoft PowerPoint 全屏放映时，手指轻点画面后补发一次空格，由 PowerPoint 推进当前动画或进入下一页。无需 ClassIsland 插件、PowerPoint 加载项、Python 或 LLM。WPS 不接入。

## 使用

1. 将便携 ZIP **完整解压**到 Windows x64 电脑；包内已经包含 .NET 运行时。
2. 双击 `Start-Touch-Assist.cmd`，或 `app/NPEduTools.PowerPoint.Assist.exe`。启动后默认开启辅助。
3. 在 PowerPoint 中开始全屏放映，轻点画面中部。动画与换页顺序由 PowerPoint 自己决定。
4. 可最小化工具窗口；点击“暂停辅助”临时关闭，点击“退出”或关闭窗口彻底停止。

默认只响应带 Windows 触摸标记的兼容鼠标事件，普通鼠标保持原行为。目标设备轻点完全无效时，可勾选“兼容未标记触摸”：此模式也会响应普通鼠标，适用于把手指上报成鼠标的部分驱动。选项仅对本次运行生效。

## 与参考项目一致的范围

- 松手时识别短轻点；轻微抖动可接受，长按和拖动不补发。
- 左、右及底部约 95 个逻辑像素避让工具栏；点击这些边缘或右键菜单后，跳过一次轻点，避免关菜单时换页。
- 仅对当前前台、光标命中的 PowerPoint 放映窗口生效。退出放映后自动等待；画笔、橡皮及暂停放映时暂不辅助。
- 补发前读取更新后的放映位置；若检测到原生动画或页面已经推进，就不再补发。不是拦截输入，不修改文稿，也不安装全局键盘钩子。
- 空格消息直接发送到已经核实的放映窗口。窗口中的“已补发”表示消息已入队，不是 PowerPoint 已完成动画的计数。

这是一款按参考项目范围实现的轻量工具，没有完整的超链接、媒体控件和触发器命中识别。操作这类交互内容时可以暂停辅助；如果设备自身已经能轻点推进，也可以直接关闭。仅支持一个 PowerPoint 进程中的单个全屏放映；窗口化放映不启用。权限不同导致发送失败时会显示原因，不自动提权。

## 开发与验证

源码项目：`src/NPEduTools.PowerPoint.Assist`；输入与辅助引擎复用 `src/NPEduTools.PowerPoint.Diagnostics` 中的 Windows 探测基础设施。原来的只读诊断与显式单次实验仍独立保留，自动辅助不受实验的简单 Appear 文稿限制。

在仓库根目录运行：

```powershell
./scripts/start-powerpoint-assist.ps1
./scripts/package-powerpoint-assist.ps1
```

自动联调：构建 Debug 解决方案后运行 `./scripts/test-powerpoint-assist.ps1`。脚本要求事先关闭 PowerPoint，创建自己的两页文稿，将带触摸标记的模拟输入送到已核实的测试放映窗口，验证 Fade 动画、下一页、画笔模式和 WPF 子进程协议；结束后清理自己创建的文稿和进程，证据写入 `.artifacts/powerpoint-assist-live/`。开发高级入口 `NPEduTools.PowerPoint.Diagnostics.exe --assist-seconds 120` 会开启最多 120 秒的自动辅助，并输出状态 JSON；其余诊断入口保持原行为。

2026-09-06：102 项回归测试通过；开发机真实 PowerPoint（Office 16.0.20326.20132）的模拟触摸自动推进链路、触摸期间 COM 已推进时跳过补发、便携版后台协议及窗口启动/关闭均已通过。目标为用户的 **Office 2024 触摸大屏**，物理触摸及该设备驱动尚待现场试用；模拟输入不等同于实机触摸验证。

## 参考来源

交互思路来自仓库 `docs/PowerPoint-Touch-Assist/func.py`，参考代码许可为 CC0 1.0，便携包保留其许可副本。采用独立 C# 实现，增加触摸来源筛选、前台窗口校验、DPI 换算及暂停/退出入口。

Windows API：微软 [触摸与鼠标消息标记](https://learn.microsoft.com/en-us/windows/win32/tablet/system-events-and-mouse-messages)、[PostMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)、[WM_KEYDOWN](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-keydown)。
