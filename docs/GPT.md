看完这一圈项目之后，我觉得 NPEduTools 的定位其实已经很清楚了，而且越清楚，越应该**克制**。

我会把它定义成：

> **NPEduTools = 面向教室大屏的本地集成、编排与控制层。**
>
> 它不负责“再做一遍白板、计时器、点名器、课表、作业板”，而是负责让这些已经存在的软件**更容易被控制、彼此联动、在不同课堂场景下协同工作**。

它和其他项目的边界可以这样看：

```
C30 / 希沃 / 鸿合
→ 提供教学工具

ClassIsland
→ 提供课表与课堂状态

NPClassworks
→ 提供作业、通知、班级信息

ExamAware
→ 提供考试态势

Luminalium
→ 提供演示辅助

SecAgent
→ 用 AI 理解“用户想干什么”

NPEduTools
→ 确定性地把这些东西组织起来、控制起来、联动起来
```

我觉得最适合它的一句话甚至不是“工具箱”，而是：

> **Classroom Integration Runtime**
>
> 教室软件集成运行时。

它应该更像“胶水层 + 控制层”，而不是“万能软件”。

第一版真正值得做的东西，大概就这些：

- **应用控制**：启动、退出、隐藏、恢复、重启 ClassIsland、NPClassworks、ExamAware、PPT/WPS 等。
- **演示增强**：把 PowerPoint Touch Assist 的核心逻辑重写成一个模块，这类能力是 C30 不一定能替代的。
- **场景编排**：上课、考试、自习、放学等场景，一次执行多个确定动作。
- **状态探测**：软件有没有装、什么版本、是否正在运行、接口是否健康、当前有哪些能力可用。
- **统一 Capability 层**：例如 `classisland.restart`、`presentation.touch.enable`、`npclassworks.open`，UI、快捷键、场景、API 都调用同一个能力。
- **外部集成接口**：未来让 SecAgent、NPClassworks、其他程序通过 Named Pipe / localhost API 调用 NPEduTools。
- **可靠性基础设施**：日志、健康检查、配置迁移、崩溃恢复、原子写入、更新回滚、超时与取消。

这就够了。

真正重要的是：**NPEduTools 的价值不是功能数量，而是“把现有生态之间那些没人管的缝补上”。**

比如：

```
老师点击“考试模式”

NPEduTools
→ 检测 ExamAware
→ 隐藏/退出 ClassIsland
→ 关闭 NPClassworks 展示
→ 禁用 PPT Touch Assist
→ 启动 ExamAware
→ 验证每一步结果
→ 返回“考试模式已就绪”
```

这就很有价值。

而不是：

```
考试模式
→ 自己再写一个考试倒计时
```

那就开始跟 ExamAware 抢饭碗了。

------

反过来说，我觉得 NPEduTools 有几件事应该**非常明确地避免**。

最重要的是避免“功能膨胀”。我们已经看过 Luminalium 是怎么长大的：PPT 辅助 → 白板 → 放大 → 聚光 → 计时器 → 启动器 → 设置 → WebView → QML → 内存清理……最后就变成一个大型平台。NPEduTools 不应该走这条路。

它还应该避免这些：

- **不要重复造 C30 已经成熟的轮子**。白板、聚光灯、截图、计时器、随机点名，只要已有方案够用，就不要做。
- **不要 AI-first**。SecAgent 可以做“脑子”，但 NPEduTools 自己必须在无网、无 API Key、AI 服务挂掉时照常工作。
- **不要依赖预览版生态**。稳定版优先，实验版只提供增强能力。不能为了联动一个软件，逼教室升级到官方明确不建议生产使用的测试版。
- **不要靠标题字符串和 `taskkill` 到处硬匹配**。能用正式 API、IPC、URI、插件接口，就不要靠窗口标题猜。必要时可以 fallback，但不能成为核心。
- **不要把 arbitrary shell 当成插件系统**。`cmd /c xxx`、PowerShell 一把梭很爽，但最终会让安全边界和维护性一起消失。
- **不要无界执行**。所有 Capability 都应该有 timeout、cancel、结果状态；场景执行要能知道“哪一步失败了”。
- **不要用非事务式更新**。更新模块、配置、插件之前要 staging，成功后再切换，失败自动回滚。
- **不要把配置直接 `writeFile` 覆盖**。教室突然断电很常见，配置应该临时文件 + 原子替换 + 备份。
- **不要让插件和宿主同生共死**。未来插件系统如果真的出现，重插件最好进独立进程；至少要有故障隔离。
- **不要为了跨平台牺牲 Windows 体验**。NPEduTools 的核心场景就是 Windows 大屏，就应该承认这一点。
- **不要上 Electron 当核心宿主**。不是 Electron 不能做，而是全天常驻、透明窗口、系统控制这种场景，用 C#/.NET 更合适。

我尤其想强调最后一点：

> **NPEduTools 不应该以“支持所有平台”为荣，而应该以“在教室 Windows 大屏上一整天不出事”为荣。**

------

所以技术上，我现在会坚定选：

```
C# + .NET
WPF
Win32 / PInvoke
Named Pipe
HTTP localhost（必要时）
```

内部结构可以很简单：

```
NPEduTools.App
│
├─ Core
│  ├─ CapabilityRegistry
│  ├─ SceneEngine
│  ├─ HealthMonitor
│  ├─ Config
│  ├─ Logging
│  └─ IPC
│
├─ Integrations
│  ├─ ClassIsland
│  ├─ NPClassworks
│  ├─ ExamAware
│  └─ PowerPoint
│
└─ UI
   ├─ Tray
   ├─ QuickPanel
   └─ Settings
```

我甚至暂时都不会叫它“插件系统”。

先叫：

**Integrations / Adapters**

就够了。

等哪天真的有第三方开发者说：

> “我想给 NPEduTools 加一个希沃适配器。”

那时候再抽象 Plugin SDK。

否则很容易出现我们前面说的那个经典场面：

> 插件系统 4000 行，插件 1 个。

------

如果要把整个项目的设计哲学压缩成五句话，我会写：

> **不重复已有工具。**
> **优先连接，而不是替代。**
> **稳定版优先于新功能。**
> **所有动作必须有界、可取消、可恢复、可审计。**
> **UI 可以坏，网络可以断，AI 可以挂，但核心控制层必须继续活着。**

我觉得这五条，已经足够决定 NPEduTools 后面 80% 的架构选择了。

---

可以，而且我觉得现在最有价值的不是“从每个项目抄一个功能”，而是**从每个项目抽一条最值得继承的工程思想**。

我会把 NPEduTools 的“师承关系”概括成这样：

| 来源                    | 最值得借鉴的东西                                             | NPEduTools 应怎样吸收                                        |
| ----------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| PowerPoint Touch Assist | **极简、单一职责**                                           | PresentationAssist 只做演示交互，不长成第二个 Luminalium     |
| Luminalium              | **PPT/WPS 抽象、Overlay、模块化演示能力**                    | 做 `PresentationAdapter`，Office/WPS 各自适配；不要照搬 WebEngine/GUI 复杂度 |
| Sidebar for Class       | **生命周期编排、Guardian、窗口/进程监控、自动化**            | 做 `SceneEngine`、`ProcessMonitor`、退出恢复机制，但不要“看标题就 taskkill” |
| ClassSidebar            | **工程重构、分层、类型化配置、迁移**                         | Core / Integrations / UI 分离；配置有 Schema 和 Migration    |
| SecAgent                | **Capability/Tool 思想、健康探测、连接器、审计、更新安全、测试纪律** | 作为 NPEduTools 核心架构骨架，但去掉 AI 依赖                 |
| ClassIsland             | **稳定插件接口、宿主/插件解耦**                              | 能走正式 API/插件接口就不要直接改别人数据文件                |

------

## PowerPoint Touch Assist：借它的“克制”

PTA 最值得学的反而不是 Python 代码，而是：

> **一个功能只解决一个明确问题。**

它的核心逻辑非常容易说明：

```
检测演示状态
+
监听触摸/点击
+
识别有效点击
+
执行翻页
```

所以 NPEduTools 里的 PresentationAssist 也应该保持这个味道：

```
PresentationAssist
├─ Detect
├─ Gesture
├─ Control
└─ Settings
```

不要发展成：

```
PresentationAssist
├─ 白板
├─ 截图
├─ 聚光灯
├─ 计时器
├─ 文件管理
├─ 点名
└─ AI
```

不然就又开始重走 Luminalium 的路。

------

## Luminalium：借“演示软件抽象”，不要借它的技术债

Luminalium 很值得参考的一点是：它已经认真面对了 **PowerPoint + WPS + Windows/Linux + Overlay + 触控** 这些现实问题，而不是假设所有环境都一样。README 也明确把 Office 和 WPS、Windows 和 Linux 都作为兼容目标。

所以 NPEduTools 可以直接形成：

```
IPresentationAdapter
```

下面分别：

```
PowerPointAdapter
WpsPresentationAdapter
```

然后上层只关心：

```
IsPresenting
Next
Previous
GoToSlide
EnableTouchAssist
```

而不是：

```
if (PowerPoint) ...
else if (WPS) ...
```

散落几十处。

Luminalium 还提醒了我们另一件事：**Overlay 和 WebView 是很容易把项目拖复杂的区域。**它后来甚至专门做了 memory cleaner 去管理 QtWebEngine 相关进程和工作集。

所以我们借：

> 演示适配层。

不借：

> “为了几个 UI 页面，养一整套 WebEngine，再写内存清理器追着它跑。”

------

# Sidebar for Class：这是 NPEduTools 最应该研究的“产品祖先”

它和我们现在的定位最接近。

它已经出现了：

```
automation
guardian
killer
window-history
launcher
system
tray
```

这种结构。

其中 **Guardian** 特别值得借。

它的思想是：

```
主程序运行
↓
守护状态

主程序意外退出
↓
恢复之前被修改的外部软件状态
↓
执行 shutdown actions
```

比如 ICC 兼容模式下，它会在主程序退出后恢复 ICC-CE。

这个思想对 NPEduTools 非常重要。

例如：

```
进入考试模式

NPEduTools
→ 隐藏 ClassIsland
→ 关闭 PresentationAssist
→ 启动 ExamAware
```

然后 NPEduTools 自己突然崩了。

一个成熟系统不能留下：

```
ClassIsland 永久隐藏
PresentationAssist 状态未知
ExamAware 卡着
```

所以应该有：

```
SceneState
+
RecoveryPlan
```

甚至记录：

```
{
  "scene": "exam",
  "changes": [
    {"target":"ClassIsland","previous":"visible","current":"hidden"},
    {"target":"PresentationAssist","previous":"enabled","current":"disabled"}
  ]
}
```

异常恢复时按相反方向回滚。

### Killer 也值得借，但只能借一半

Sidebar for Class 的 Killer 会根据窗口标题、进程名甚至窗口尺寸识别和关闭其他工具。

值得借的是：

> **主动处理软件冲突。**

不值得借的是：

> **标题包含“计时器”？杀。**

NPEduTools 应该升级成：

```
正式 API
↓
插件/IPC
↓
进程身份 + HWND
↓
窗口标题 fallback
```

而不是反过来。

------

# ClassSidebar：借它“第二次重构以后才有的东西”

这个项目最值得 NPEduTools 借的不是具体功能，而是：

> **早期项目长大以后，应该重新整理架构。**

ClassSidebar 后来已经很清晰地分成：

```
src/
├─ main
├─ preload
└─ renderer
```

主进程又分：

```
SidebarWindow
TrayManager
SystemToolManager
store
```

NPEduTools 可以直接把这个思想翻译成 .NET：

```
NPEduTools
├─ App
├─ Core
├─ Integrations
├─ Infrastructure
└─ UI
```

ClassSidebar 的配置也有比较明确的 typed schema 和旧配置迁移，例如旧 Widget 没 UUID 时自动补 UUID。

所以 NPEduTools 的配置从第一天就应该有：

```
ConfigVersion
Schema
Migration
Validation
Backup
AtomicWrite
```

而不是：

```
settings.json
```

然后 v0.5：

> “怎么改字段以后全体用户配置炸了？”

------

# SecAgent：最应该借它的“骨架”

SecAgent 是目前这些项目里，**最适合借架构思想、最不适合借产品定位**的。

它最值得搬到 NPEduTools 的，是：

## Capability / Tool 模型

不要让：

```
Button_Click
```

直接：

```
Process.Kill(...)
```

而应该：

```
UI
      ↓
CapabilityRegistry
      ↓
classisland.restart
      ↓
ClassIslandAdapter
```

这样未来：

```
快捷键
Scene
Local API
SecAgent
CLI
```

全部调用同一个东西。

例如：

```
classisland.start
classisland.stop
classisland.restart
classisland.hide
classisland.show

presentation.touch.enable
presentation.touch.disable

npclassworks.open
npclassworks.refresh

examaware.start

scene.class.start
scene.exam.start
scene.restore
```

这会成为 NPEduTools **最核心的一层**。

------

## SecAgent 的“可选依赖失败不拖死整个系统”也非常值得学

它的 MCP discovery 会记录某个连接失败，但不会因此让全部工具不可用；HTTP/stdio 调用都有明确超时。

NPEduTools 也应该：

```
ClassIsland Adapter：Offline
NPClassworks Adapter：Healthy
PowerPoint Adapter：Not Installed
ExamAware Adapter：Healthy
```

而不是：

```
ClassIsland 出问题
↓
NPEduTools 启动失败
```

这叫：

> **故障域隔离。**

------

## SecAgent 的更新系统值得大量抄作业

这可能是它目前最成熟的一块：

```
签名更新元数据
SHA-256
下载校验
临时文件
原子替换
超时
代理失败 → 直连 fallback
```

NPEduTools 的 updater 我甚至会建议直接拿它当设计参考。

学校软件自动更新最怕：

```
更新一半断电
↓
第二天上课
↓
.exe：无
```

所以更新必须：

```
Download
↓
Verify
↓
Stage
↓
Switch
↓
Health Check

失败：
Rollback
```

------

## 还有 SecAgent 的测试态度

它 CI 会编译、测试，然后在 Windows/Linux/macOS 打包。

NPEduTools 不一定需要三平台，但至少应该：

```
Windows CI
├─ build
├─ unit tests
├─ integration tests
└─ package
```

尤其这几类测试必须从早期就有：

```
版本比较
配置迁移
Capability timeout
Scene rollback
进程发现
PowerPoint/WPS detection
ClassIsland detection
重复执行幂等性
异常退出恢复
```

这些远比“按钮颜色对不对”的测试重要。

------

# 最后，我会把这些项目的精华浓缩成 NPEduTools 的六个核心子系统

```
                NPEduTools
                     │
        ┌────────────┼────────────┐
        │            │            │
 Capability      SceneEngine   HealthMonitor
 Registry
        │
        ▼
 Integration Adapters
 ├─ ClassIsland
 ├─ NPClassworks
 ├─ ExamAware
 ├─ PowerPoint
 └─ WPS

        │
        ▼
 Infrastructure
 ├─ Process / Win32
 ├─ IPC
 ├─ Config + Migration
 ├─ Audit / Logging
 ├─ Recovery
 └─ Update
```

UI 反而应该放在最外面：

```
Tray
Quick Panel
Settings
```

**UI 不是 NPEduTools 的核心。**

------

所以我现在会说：

> PTA 教我们**克制**；
> Luminalium 教我们**抽象演示软件，也警告功能膨胀**；
> Sidebar for Class 教我们**编排和生命周期恢复**；
> ClassSidebar 教我们**重构、分层和配置迁移**；
> SecAgent 教我们**Capability、故障隔离、审计、测试和安全更新**。

如果把这些精华吸收掉，同时把它们踩过的坑绕开，NPEduTools 就不会只是“又一个班级侧边栏”，而会真正变成一套**教室软件运行协调基础设施**。