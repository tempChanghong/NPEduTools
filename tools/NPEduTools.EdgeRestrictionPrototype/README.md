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
- `--watch-foreground` 只读前台 Edge Stable 的 UI Automation。只有同一前台窗口的活动文档可证实是浏览器内部错误页、有管理员拦截错误码、并与非编辑态地址栏和错误文档里的目标一致时才警告。仅输入地址、网页文案、后台请求和无法读全的证据都不触发。终端 Ctrl+C 可停止观察。
- 隔离夹具同时用 [Windows MSAA](https://learn.microsoft.com/en-us/windows/win32/api/oleacc/nf-oleacc-accessibleobjectfromwindow) 读取渲染子窗口的顶层文档值，只保存分类。这个诊断结果不参与触发。

真实部署需要在有权配置 URLBlocklist 的独立浏览器主体上验证。浏览器内置的 `edge://network-error/-22` 可观察原生诊断页，但不能代替指定站点被策略实际拦截的正例。

本轮实际的 Edge Stable 158 在普通网页、网络错误及内置诊断页都能定位前台浏览器和独立的渲染子窗口，UI Automation 与 MSAA 均未读到可确认的活动文档，三种情况都保持静默。Stable 158 和独立解包的 Dev 160 的策略测试地址未提供隔离配置入口，本机未写系统策略。Windows 警告用合成导航证据单独实测通过，实际窗口在 2560×1600、150% 缩放的单个物理屏上完成；其他 DPI 与竖屏仅测布局。真实策略拦截后的自动告警仍未验证。

真实 Edge 前台教案页在观察期间收到 11 次后台 HTTP 403，没有触发警告。HTTP 403 是隔离服务器返回的，用来验证后台请求，不代表 Edge 策略拦截。复用警告界面后的原自习退出专项通过 56 项检查。
