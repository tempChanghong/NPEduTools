# Edge 网站访问警告原型

原型需要 Windows 和 .NET 10 SDK，默认 App 不会启动观察器。Microsoft Edge 的 [URLBlocklist](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/URLBlocklist) 由管理员在浏览器侧配置。本工具只观察当前前台浏览器，浏览器继续负责拦截。

```powershell
dotnet build tools/NPEduTools.EdgeRestrictionPrototype -c Release
$prototype = './tools/NPEduTools.EdgeRestrictionPrototype/bin/Release/net10.0-windows/NPEduTools.EdgeRestrictionPrototype.exe'
& $prototype --self-test .artifacts/edge-website-restriction/final
node tools/NPEduTools.EdgeRestrictionPrototype/smoke-uia.mjs
& $prototype --watch-foreground 'https://douyin.com/'
```

- `--self-test` 用合成的浏览器观察数据验证触发条件，用实际 WPF 窗口验证十语、屏幕定位和自动收起。生成窗口图和双音 WAV。另生成横屏、竖屏和不同 DPI 的布局图。
- `smoke-uia.mjs` 用独立访客配置打开真实 Edge Stable，只访问本地夹具和浏览器内置诊断页。无扩展，无调试端口。夹具日志只保存观察状态和次数。
- `--watch-foreground` 只读前台 Edge Stable 的 UI Automation。唯一活动顶层文档必须是浏览器保护的内部错误页，具有管理员拦截错误码或该页的两条精确原生提示，同时非编辑态地址栏必须匹配目标。根地址表示同一协议、主机和端口下的整个站点，可由原生错误文档、浏览器窗口或选中标签的主机名佐证。指定非根路径时，内部错误文档也必须露出确切 URL。隔离探针与普通观察采用同一判据。终端 Ctrl+C 可停止观察。
- 隔离夹具同时用 [Windows MSAA](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow) 读取渲染子窗口的顶层文档值，只保存分类。这个诊断结果不参与触发。

真实部署需要在有权配置 URLBlocklist 的独立浏览器主体上验证。浏览器内置的 `edge://network-error/-22` 可观察原生诊断页，但不能代替指定站点被策略实际拦截的正例。

初轮宿主的 Edge Stable 158 在普通网页、网络错误及内置诊断页都能定位前台浏览器和独立的渲染子窗口，UI Automation 与 MSAA 均未读到可确认的活动文档，三种情况都保持静默。Stable 158 和独立解包的 Dev 160 的策略测试地址未提供隔离配置入口，宿主未写系统策略。警告用合成导航证据单独验证了物理屏与 DPI；竖屏仅测布局。

真实 Edge 前台教案页在观察期间收到 11 次后台 HTTP 403，没有触发警告。HTTP 403 是隔离服务器返回的，用来验证后台请求，不代表 Edge 策略拦截。复用警告界面后的原自习退出专项通过 56 项检查。

## Windows 10 x64 客机的真实策略验收

在开发机发布自包含程序，再把 `app` 文件夹和 `Test-GuestEdgePolicy.ps1` 放进客机同一个隔离目录。

```powershell
dotnet publish tools/NPEduTools.EdgeRestrictionPrototype -c Release -r win-x64 --self-contained -o .artifacts/edge-policy-guest/app
```

由唯一控制客机前台的人在已登录的客机运行。脚本检测客机和互动会话，不在宿主运行。

```powershell
powershell.exe -NoProfile -File .\Test-GuestEdgePolicy.ps1 -Output C:\NpepEdgePolicyEvidence
```

脚本默认设置当前客机用户的两条临时 URLBlocklist，用随机的抖音域名路径和回环端口路径限定范围。它先访问未配置策略的回环目标，之后独立验证课堂 iframe 导航、顶层回环打开和顶层抖音路径。只用新建的 Edge 访客目录，不启动调试端口，测试结束移除脚本登记且未被替换的值。检查 `result.json` 的 `policyRestored`，如中途硬退出由客机操作者按记录核对原值。将截图、观察 JSON 和本地请求计数带回即可，无需传回 Edge 访客目录。

`-LaunchUrlDirectly` 通过标准 Edge 启动参数打开测试页面，测试仍要求核对真实前台窗口，适用于来宾自动化无法安全键入的情形。`-MachinePolicy` 仅用于客机操作者已经有相应权限且明确选择机器策略的情形。`-DiagnosticAccessibility` 为差分诊断启用 Edge 渲染辅助功能，其结果应与普通启动分开报告。

客机前台会话对策略只读时，可用 `-ExternalPolicyHandshake`。交互程序只在本次证据目录写 `policy-request.json`，含本次 `runId`、`policyHive`、回环端口和两条随机路径。获授权的客机管理员在另一会话用同包的 `Set-GuestEdgeTestPolicy.ps1` 核对请求、设置和恢复临时 HKLM 值，并按相同 `runId` 回复 `policy-ready.json` 与 `policy-restored.json`。`policy-finished.json` 表示交互验收已结束，管理员脚本也会在超时时回收。

```powershell
# 已登录的普通客机会话
powershell.exe -NoProfile -File .\Test-GuestEdgePolicy.ps1 -Output C:\NpepEdgePolicyEvidence -LaunchUrlDirectly -ExternalPolicyHandshake

# 获授权的客机管理员。将引号内的目录替换为上一步打印的真实目录。
powershell.exe -NoProfile -File .\Set-GuestEdgeTestPolicy.ps1 -RunDirectory 'C:\NpepEdgePolicyEvidence\edge-policy-实际编号'
```

在原 Windows 10 x64 客机（build 19045、Edge Stable 157.0.4341.60）用中文版普通浏览器进程实测。无扩展、调试端口或强制辅助功能。以 `https://douyin.com/` 观察同源站点，真实抖音测试路径触发一次警告，30.04 秒自动收起，同次观察期间未重弹；Windows 收到播放请求，未核实物理扬声器。仿冒两条准确中文提示的普通网页、另一个真实被策略拦截的站点与课堂子框架均无警告。课堂对照 iframe 到达回环服务，受限路径未到达，教案仍可见，客机原始政策值及键存在状态已复核恢复。该试验不覆盖所有 JavaScript 子资源请求、Edge 分屏或其他语种原生错误模板。整屏实拍中 ClassIsland 与 C30 的部分置顶栏仍可见。

## 产品链路复跑

正式教室端与原型编译同一份 Edge 观察和警告源文件。Host 仅在已核对的原生配对得到新版网站策略后修改 Windows。它在机器策略清单中使用空槽，按自己的机器级归属记录回收，遇到重占的槽位保留外部值。浏览器观察通过正式应用和 Host 的当前会话管道返回。空名单清除本产品条目，不向 Edge 写允许例外，也不代表已观察到网站成功打开。正式模式不会主动发起访问或上传访问路径。

在获准的隔离 Windows 客机，把两个 PowerShell 脚本、Console 仓的 `tests/*.mjs`、已生成 Prisma 客户端/依赖的 KV 副本、Node、可运行的 PostgreSQL、可运行的 Console ClassCreationDesktop 和教室端构建放在同一试验目录。对应子目录为 `tests`、`kv`、`pg`、`console` 和 `product`。便携 KV 依赖用原 pnpm 锁及 `--config.node-linker=hoisted` 安装，并核对 Prisma Windows schema engine 和 bcrypt 能实际加载。先在已登录的用户会话运行 `guest-product-fixture.ps1 -EvidenceDirectory <独立目录>`，备用 KV 目录使用 `-KvFolder kv-portable`。使用正式 App 从同一已登录会话启动唯一测试实例，配对驱动与该 App 一样使用现有管理员令牌。

按 `fixture.json` 的阶段运行 `guest-product-pairing.ps1`，指定同一个隔离管道名 `NPEduTools.EdgeProduct.<唯一标记>` 与证据目录。`pair-N` 用 `Begin`，`finish-pair-N` 用 `Confirm`，`unpair-N` 用 `Unpair`。浏览器阶段用独立测试配置的普通 Edge 核对，完成后写对应阶段名的 JSON 标记；Test-GuestEdgePolicy 的管理员策略桥只供上节原型试验使用。产品用例的目标网址必须由正式 Host 下发。用例结束先确证按原绑定解绑后产品值消失，再移除测试专用计划任务、浏览器配置和任意独立哨兵值。只保留去密的截图、阶段和逐屏状态证据。

已有可信的一班放行证据时，可在原测试配对已解除后用 `-NeighborOnly` 在新隔离库通过 Console 重建年级与一班例外，定向复验二班继承、告警和解绑。复验不读取前一库的回执。
