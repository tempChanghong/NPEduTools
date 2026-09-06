# NPEduTools

面向 Windows 教室大屏的本地软件集成与控制层。主程序提供屏幕贴边快捷入口和“常用／设置”主窗口，已集成 PowerPoint 触摸翻页。默认右侧偏下的 `n` 入口可上下拖动，点击即可直接开启、暂停或停止辅助。运行 `./scripts/start-app.ps1` 查看实际窗口，界面与生命周期见 [主界面设计](docs/MAIN-WINDOW-DESIGN.md)。

新增可运行的 [PowerPoint 触摸翻页工具](docs/POWERPOINT-TOUCH-ASSIST.md)：按参考项目 PowerPoint-Touch-Assist 的效果，轻点放映画面后补发空格，推进动画或下一页，提供暂停和退出。运行 `./scripts/start-powerpoint-assist.ps1`；便携包完整解压即可运行，无需安装 .NET。默认只响应触摸标记，鼠标保持原行为。开发机真实 PowerPoint 的模拟触摸链路已通过，目标 Office 2024 大屏的物理触摸仍需现场试用。

另保留独立的 [PowerPoint 触摸诊断工具](docs/POWERPOINT-DIAGNOSTICS.md)：可读取真实放映及动画状态，观察目标窗口的兼容鼠标输入并导出记录。运行 `./scripts/start-powerpoint-diagnostics.ps1`；此入口只读，不触发翻页。

诊断结束会自动生成中文报告；也可用 `./scripts/start-powerpoint-diagnostics.ps1 -Analyze <日志路径>` 分析旧日志。新版记录包含有限页面交互清单，测试包附五页动画/链接/触发器/书写测试文稿，详见上述说明。

另有显式单次推进实验：`./scripts/step-powerpoint-experiment.ps1` 会尝试推进当前 PowerPoint 的一个简单动画步骤或换页，并回读结果。仅支持已验证的简单文稿，未连接触摸监听；默认诊断模式仍只读。

当前已实现 WPF 窗口与 CLI → Named Pipe Host → 隔离工作进程 → ClassIsland IPC 的课程状态闭环，支持持续监听、自动重连和完整状态同步。WPF 还支持配置 ClassIsland 路径、启动本体、验证就绪并查看保存的启动结果。

窗口会按需启动 Host。标题栏关闭、最小化或“快捷面板”隐藏主窗口，屏幕侧边入口、触摸辅助与后台继续运行；托盘和再次打开程序也可唤回同一窗口。主窗口“退出 NPEduTools”或托盘“停止后台并退出”释放触摸监听与探测进程后完整退出，不关闭 ClassIsland、PowerPoint 或文稿。本阶段尚未实现场景执行或 NPEduTools 自身的开机启动；ClassIsland 管理员重启与自启动由独立管理区显式操作。

## 打开桌面窗口

新增 [ClassIsland 管理员自启动管理](docs/CLASSISLAND-ADMIN-STARTUP.md)：首页 ClassIsland 的“自启动”进入管理区，可查询、创建／更新、删除 StartUpAsAdmin 使用的同名任务，以及请求管理员启动／重启。提权由 Windows UAC 授权，NPEduTools 主窗口保持普通权限。用户已实测授权后创建任务成功，并确认普通／管理员状态显示大体正常；删除、重新登录自启动及完整重启链路等剩余验收见说明。

完成下述构建验证后，从仓库根目录运行：

```powershell
./scripts/start-app.ps1
```

首页提供触摸翻页开关、暂停／继续及 ClassIsland 启动入口。首次运行默认关闭触摸辅助；后台断开时会清除可用状态并重新连接，不自动重新启用已因后台退出而停止的辅助。独立辅助版和主程序共用互斥检查，不能同时运行两套监听。

“设置”中可切换快捷入口的左／右侧、选择触摸兼容模式、配置 ClassIsland 程序位置，并展开课程、课表和连接详情。入口位置会在重启后保留。ClassIsland 未运行不影响触摸辅助；目标大屏现场试用暂缓。

首次使用：点击“选择文件…”选择 `ClassIsland.exe` 或 `ClassIsland.Desktop.exe`，点击“保存路径”，再点击“启动 ClassIsland”。已有对应实例时只验证接口，不重复启动；只有读到课程状态后才记录“已就绪”。首次运行的许可、隐私同意等向导需要用户在 ClassIsland 中完成；接口未就绪时会记录超时，稍后可以再次点击启动以验证已有进程。

路径和最近启动结果会在窗口、Host 重启后保留。默认保存位置为 `%LocalAppData%/NPEduTools/config/classisland.json`，使用版本化 JSON、原子替换及备份。损坏记录不会被静默重置或自动重放，详情见 [启动与持久化记录](docs/M1-LAUNCH-VALIDATION.md)。

也可以直接打开 `src/NPEduTools.App/bin/Release/net10.0-windows/NPEduTools.App.exe`，请保留旁边的 `Host` 子目录。桌面窗口需要 .NET 10 Desktop Runtime。当前交付为构建目录，尚未制作安装包或验证 `dotnet publish` 分发。

实现细节、验收证据与剩余范围见 [M1 只读桌面验收记录](docs/M1-READONLY-VALIDATION.md)和 [M1 启动验收记录](docs/M1-LAUNCH-VALIDATION.md)。

## 开发环境

- Windows x64；本次验证环境为 Windows build 26200。
- .NET SDK `10.0.400`，由 `global.json` 固定；后台目标为 `net10.0`，WPF 为 `net10.0-windows`。
- PowerShell，初次还原依赖时需要连接 NuGet。

当前开发机已安装系统 SDK 8.0.424、9.0.317 和 10.0.400。NPEduTools 使用 10.0.400，旁边的 ClassIsland 源码使用 9.0.317。可以直接使用 `dotnet`，或通过包装脚本完成验证：

```powershell
./scripts/verify.ps1
```

`scripts/dotnet.ps1` 优先使用能满足本项目 `global.json` 的系统 SDK，必要时回退到已有的项目内 SDK；测试子进程沿用同一个 dotnet 主机。CLI 数据和 NuGet 缓存仍位于 `.tools`。`.tools`、`.artifacts` 和构建输出均不提交到 Git。

没有匹配 SDK 的机器可选用 `./scripts/bootstrap-sdk.ps1` 下载独立 SDK；脚本会校验官方 SHA-512。当前开发机无需执行此安装步骤。

ClassIsland 本地开发环境与文档索引见 [本地联调环境](docs/LOCAL-DEVELOPMENT.md)。

## 查询真实 ClassIsland

先完成构建，在普通用户会话中启动 ClassIsland。当前适配目标为 `ClassIsland.Shared.IPC 2.1.0.1`，默认管道为 `ClassIsland.IPC.v2.Server`。本地 2.1.0.1 Debug 本体的真实状态、事件和重启联调已通过；正式发行包及教室环境尚待验收。

终端一启动 Host：

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Host --configuration Release --no-build --no-restore
```

终端二查询：

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' ping
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' status
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' status --timeout-ms 5000 --observe-ms 1000
```

通过 PowerShell 包装脚本调用 `dotnet run` 时，请保留示例中 `'--'` 的引号，否则 PowerShell 会移除该参数分隔符。

Host 默认管道按用户 SID 和会话区分，仅接受同一用户、同一会话的客户端。启动失败或权限不一致会明确报错，不自动提权。

`--timeout-ms` 范围为 250–15000，包含探测进程启动、连接、观察及读取时间。`--observe-ms` 范围为 0–5000，必须小于超时时间。课程事件仅统计本次连接观察到的事件，零次事件不代表连接前没有发生事件。

Host 用 Ctrl+C 停止；CLI 可以独立退出并重新查询。CLI 退出不会关闭 Host；已受理的查询继续执行至完成或期限结束。查询超时最多额外需要 2 秒清理探测进程。Host 意外退出时，孤立探测进程最多存活约 20 秒。

### 持续监听

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' watch
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' stop
```

`watch` 持续输出每行一条 JSON，Ctrl+C 结束客户端监听。Host 共享一条常驻上游连接，事件触发状态读取，并定期补充同步；界面和 CLI 最多同时占用两个订阅槽位。目标断线后使用有上限的退避重连，失败快照的 `status` 为 `null`。

每帧都是完整快照：`streamId` 区分 Host 监听实例，`sequence` 标识状态更新，`connectionId` 区分上游连接；事件计数仅对同一个 `connectionId` 累加有效。心跳可以重复序号，跳号无需补增量。断线期间的历史事件无法补发，重连以完整课程状态为准。CLI 的 Host 连接断开时会退出，桌面窗口会自动重连。

`stop` 仅停止 NPEduTools Host 及其工作进程，不关闭 ClassIsland。窗口的停止按钮还会等待后台结束；CLI 响应表示请求已受理。已连接窗口收到主动停止通知后不会自动重启 Host，重新打开窗口可以再次启动。

### 输出与错误

CLI 在标准输出返回 JSON，在标准错误输出连接问题。失败结果的 `status` 为 `null`，不会用空科目或默认枚举伪装成成功。

| 退出码 | 含义 |
| --- | --- |
| 0 | 查询成功 |
| 2 | 参数错误 |
| 3 | Host 不可达、协议/响应错误 |
| 4 | Host 已响应，但查询失败、不可用、超时或资源忙 |
| 130 | 用户通过 Ctrl+C 取消客户端等待 |

ClassIsland 未运行时可能返回 `ClassIslandDeadlineExceeded`，它仅说明没有在期限内完成查询，不能据此判断软件未安装。权限拒绝、已连接接口超时和连接断开分别使用不同错误码。

Host 诊断日志写入标准错误，仅记录请求标识、结果、耗时及脱敏错误类别，不记录科目正文。启动操作另外保存执行记录与 RequestId 去重信息；课程查询不保存历史。单次查询不自动重试，常驻只读监听自动重连。文件日志轮转和诊断导出仍未实现。

## 无 ClassIsland 时的演示

测试服务端使用官方 IPC 契约，通过私有测试管道返回模拟课程，不占用真实 ClassIsland 管道。

终端一：

```powershell
./scripts/dotnet.ps1 run --project tests/NPEduTools.ClassIsland.TestPeer --configuration Release --no-build --no-restore '--' NPEduTools.Test.demo healthy
```

终端二：

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Host --configuration Release --no-build --no-restore '--' --pipe NPEduTools.Test.host --classisland-pipe NPEduTools.Test.demo
```

终端三：

```powershell
./scripts/dotnet.ps1 run --project src/NPEduTools.Cli --configuration Release --no-build --no-restore '--' status --pipe NPEduTools.Test.host --timeout-ms 5000 --observe-ms 1000
```

模拟服务端约 60 秒后自动退出，也可用 Ctrl+C 提前结束。其他测试模式为 `empty`、`hang`、`drop` 和 `error`，分别模拟无课表、接口阻塞、查询中退出和服务异常。

## 验证记录

[M0 兼容性与验证记录](docs/M0-VALIDATION.md) 记录固定依赖、源码发现、自动化测试结果及尚未完成的实机验证。

[真实 ClassIsland 联调记录](docs/CLASSISLAND-LIVE-VALIDATION.md) 提供本体测试证据。已构建 ClassIsland Debug 本体时，可以运行：

```powershell
./scripts/test-classisland-live.ps1
./scripts/test-classisland-live.ps1 -Watch
./scripts/test-app-smoke.ps1
```

脚本会复制构建产物、生成临时课表并启动真实本体，观察自然事件，再关闭并重启本次创建的实例。已有 ClassIsland 正在运行时会拒绝启动；整个过程约一分钟，结果保存在 `.artifacts/classisland-live/`。

`-Watch` 通过同一订阅观察课程变化及本体重启；窗口验收脚本使用私有模拟服务端，通过实际 WPF 控件验证自动启动、窗口重开和恢复，保存结果与截图到 `.artifacts/app-smoke/`。这些 IPC 测试需要普通用户权限下的本机命名管道访问，受限开发沙箱可能拒绝第三方管道连接。

根目录 [LICENSE](LICENSE) 为 GPL v3。第三方依赖的许可与固定版本见 [依赖说明](docs/DEPENDENCIES.md)。
