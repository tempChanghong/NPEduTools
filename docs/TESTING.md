# 测试与验收

[文档首页](README.md) · [开发指南](DEVELOPMENT.md)

## 选择入口

| 改动范围 | 在仓库根目录运行 | 覆盖范围 |
| --- | --- | --- |
| 桌面常规开发 | `./scripts/verify.ps1` | 锁定还原、Release 构建、解决方案测试 |
| 文档整理 | `node scripts/check-doc-links.mjs` | 本仓库 Markdown 的本地文件目标；远程 URL、标题锚点和外部资料单列 |
| 快捷启动配置恢复 | [校验与隔离窗口检查](iterations/HOST-INVALID-DATA-RECOVERY-20261008.md) | 无效文件、保存拒绝、编辑输入提示、原文件保留和同窗重新读取；不启动真实快捷项目 |
| 三端整体回归 | `./scripts/test-npep-current.ps1 -Browser -Database` | 当前三仓组合、协议、桌面、网页、临时数据库与 HTTP 检查 |
| 定时监测与保护 | `./scripts/test-npep-noise-schedules.ps1 -Guard -Display -Protection -Presence` | 定时监测、显示、管理验证、在线状态和守护的专项自动检查；可加 `-Browser -Database` |
| 定时监测规则更新 | [合成排程与文件锁检查](iterations/NOISE-SCHEDULE-WINDOW-UPDATE-20261008.md#验证) | 运行时段更新、停止／故障记录及保存失败；不启动真实采集 |
| 返回作业板校时恢复 | [合成时钟与重启检查](iterations/NOISE-RETURN-CLOCK-RECOVERY-20261008.md#验证) | 校时后的已确认期限、离线申请确认和缓存重读；不调整系统时间或连接学校 |
| 初始设置学校时间 | [挂起查询与隔离 WPF 导航检查](iterations/OOBE-CLOCK-VISIT-RECOVERY-20261008.md#验证) | 路径、连接、时间三个读取阶段的旧回执／错误隔离及重新检查；不启动真实软件 |
| 管理验证窗口关闭 | [挂起状态与隔离窗口检查](iterations/NOISE-MANAGEMENT-OWNER-20261008.md#验证) | 窗口关闭后的设置／授权入口及正常窗口对照；不修改真实口令或停止监测 |
| 录课计划日期导航 | [挂起课表与隔离日期按钮检查](iterations/RECORDING-CALENDAR-NAVIGATION-20261008.md#验证) | 前后切换、返回同一天、旧断连及新日期读取；不启动实际录课 |
| 噪音状态界面 | [构建并运行隔离 WPF 检查](iterations/NOISE-STATUS-PRESENTATION-20261007.md#验证) | 当前／历史统计、断连与恢复、最小尺寸布局；不启动真实采集 |
| 噪音监测协议恢复 | [协议与隔离监测页检查](iterations/NOISE-PROTOCOL-RECOVERY-20261007.md#验证) | 无效设备／状态／操作回执、旧数据失效和轮询恢复；只用合成麦克风 |
| 定时监测管理验证 | [协议与隔离弹窗检查](iterations/NOISE-MANAGEMENT-RECOVERY-20261007.md#验证) | 无效管理回执、授权范围及显式重试；不修改真实口令或停止监测 |
| 自动录课状态界面 | [构建并运行隔离 WPF 检查](iterations/AUTOMATIC-RECORDING-STATUS-20261007.md#验证) | 设置验证、实际状态与操作反馈、记录和最小尺寸布局；不执行真实录课命令 |
| 录课计划保存反馈 | [文件锁与隔离 WPF 检查](iterations/RECORDING-PLAN-SAVE-FEEDBACK-20261007.md#验证) | 计划写入失败、试运行记录写入失败及重开恢复；不启动真实录制 |
| 自动录课命令恢复 | [模拟管道与隔离 WPF 控件检查](iterations/AUTOMATIC-RECORDING-RECEIPT-RECOVERY-20261007.md#验证) | 丢失／无效回执、明确拒绝及轮询恢复；不启动真实录制 |
| 录制轮询协议恢复 | [协议与模拟管道检查](iterations/RECORDING-POLL-PROTOCOL-RECOVERY-20261007.md#验证) | 无效状态／租约回执、状态待核实及正常轮询恢复；不启动真实录制 |
| 手动录制命令恢复 | [录制契约与模拟管道检查](iterations/MANUAL-RECORDING-RECEIPT-RECOVERY-20261007.md#验证) | 无效命令回执、解除等待、明确拒绝及轮询核实；不启动真实录制 |
| 录制设置文件恢复 | [文件与隔离 WPF 检查](iterations/RECORDING-PREFERENCES-RECOVERY-20261007.md#验证) | 无效设置、原文件保留、正常保存及重开恢复；仅使用合成设备 |
| 手动录课恢复 | [客户端与隔离 WPF 检查](iterations/MANUAL-RECORDING-RECOVERY-20261007.md#验证) | 断连、重新确认、旧查询时序与回执丢失；使用唯一模拟管道，不启动真实录制 |
| 点名回执恢复 | [SecRandom 专项与隔离 WPF 检查](iterations/SECRANDOM-STATE-RECOVERY-20261007.md#验证) | 页面与侧栏失联、旧查询时序、历史回执和恢复；不执行真实抽取 |
| 考试看板恢复 | [ExamAware 专项与隔离 WPF 检查](iterations/EXAMAWARE-STATE-RECOVERY-20261007.md#验证) | 方案与操作请求时序、失联恢复及配对导出失败；不操作真实软件或自启动 |
| 考试看板快捷入口 | [协议与隔离事件检查](iterations/EXAMAWARE-QUICK-RECOVERY-20261007.md#验证) | 无效启动回执、管理页状态核实、正常接受及关闭时取消；不启动真实软件 |
| 考试看板协议恢复 | [协议与隔离管理页检查](iterations/EXAMAWARE-PROTOCOL-RECOVERY-20261007.md#验证) | 无效状态／操作／配对回执、旧查询错误及只读恢复；不启动真实软件或写入真实配对 |
| 本机课堂模式恢复 | [课堂模式专项与隔离 WPF 检查](iterations/CLASSROOM-STATE-RECOVERY-20261007.md#验证) | 断连、新旧查询顺序与过期配置检查；只读模拟请求，不切换真实软件或自启动 |
| 主页课堂模式轮询 | [模拟协议与隔离主页检查](iterations/CLASSROOM-HOME-POLL-RECOVERY-20261007.md#验证) | 无效回执、旧模式失效和同一轮询恢复；不执行真实切换 |
| 课堂模式协议恢复 | [协议与隔离管理页检查](iterations/CLASSROOM-PROTOCOL-RECOVERY-20261007.md#验证) | 无效查询／核实回执、旧查询错误与状态恢复；只读模拟请求，不执行真实切换 |
| 定时监测返回回执 | [展示专项与隔离 WPF 检查](iterations/SCHEDULED-DISPLAY-RETURN-20261007.md#验证) | 原生备用页面的新旧会话、返回回执与失败重试；不启动采集或连接学校 |
| 定时监测返回协议恢复 | [协议与隔离 WPF 检查](iterations/SCHEDULED-RETURN-PROTOCOL-RECOVERY-20261007.md#验证) | 无效回执、旧会话隔离及显式重试；只发送模拟返回请求 |
| 学校互联协议恢复 | [共享会话与隔离 WPF 检查](iterations/SCHOOL-CONNECTION-PROTOCOL-RECOVERY-20261007.md#验证) | 无效回执、旧状态失效和只读恢复；不连接真实学校或创建真实配对 |
| 远程考试检查恢复 | [按钮事件与隔离 WPF 检查](iterations/REMOTE-EXAM-INSPECTION-RECOVERY-20261007.md#验证) | 失败后未知状态、只读恢复与重新核实；不执行软件切换或解除暂停 |
| 学校通知列表恢复 | [通知轮询与隔离 WPF 检查](iterations/NOTIFICATION-INBOX-RECOVERY-20261007.md#验证) | 未确认条目与分页失效、新列表及空列表恢复；只轮询模拟列表，不显示通知弹窗 |
| 学校通知正文有效性 | [模拟计时与隔离字段检查](iterations/NOTIFICATION-VALIDITY-RECOVERY-20261007.md#验证) | 正文核对失败、保留期限与有效恢复；不显示弹窗或发送展示／关闭回执 |
| 学校通知分页恢复 | [分页边界与隔离 WPF 检查](iterations/NOTIFICATION-PAGINATION-RECOVERY-20261007.md#验证) | 越界页重置、即时按钮状态及下轮第一页恢复；只轮询模拟列表 |
| 学校通知翻页时序 | [挂起查询与隔离按钮检查](iterations/NOTIFICATION-PAGE-ORDERING-20261007.md#验证) | 旧页结果／错误隔离、新页恢复及正文核验；不显示弹窗或发送回执 |
| 手动打开通知时序 | [并行列表与隔离打开检查](iterations/NOTIFICATION-OPEN-ORDERING-20261007.md#验证) | 旧正文错误、新页及新归属提示；禁止真实通知展示 |
| 通知窗口核验时序 | [窗口替换与隔离字段检查](iterations/NOTIFICATION-WINDOW-ORDERING-20261007.md#验证) | 旧窗口回执隔离、新窗口核验期限与继续查询；不显示弹窗或发送回执 |
| 初始设置查询恢复 | [引导专项与隔离 WPF 检查](iterations/OOBE-PREPARATION-RECOVERY-20261007.md#验证) | 准备页导航、旧查询与新查询、协议及引导记录；不执行真实配置或采集 |
| 触摸辅助状态恢复 | [合成手势与隔离主窗口检查](iterations/TOUCH-STATE-RECOVERY-20261007.md#验证) | 查询时序、丢失操作回执和状态恢复；不启动输入钩子或 PowerPoint |
| ClassIsland 管理状态 | [任务策略与隔离主窗口检查](iterations/CLASSISLAND-ADMIN-STATE-20261007.md#验证) | 检查失败、丢失回执与路径变更；不调用真实管理员工具或计划任务 |
| ClassIsland 配置恢复 | [启动服务与隔离主窗口检查](iterations/CLASSISLAND-CONFIGURATION-RECOVERY-20261007.md#验证) | 读取与保存顺序、旧查询错误及丢失回执恢复；只连接唯一模拟管道 |
| ClassIsland 启动恢复 | [启动策略与隔离主窗口检查](iterations/CLASSISLAND-LAUNCH-RECOVERY-20261007.md#验证) | 启动未知、取消授权、生命周期停止与课程验证结果恢复；不启动真实软件或管理员工具 |
| 桌面交付 | `./scripts/test-desktop-delivery.ps1 -PackageResultPath '<打包返回的 result.json>'` | 同次完整候选的 EXE／ZIP／载荷一致性、隔离安装生命周期和合成媒体 |

入口依赖和可选参数以对应脚本为准。上表的 CURRENT、定时监测与交付入口支持 Windows PowerShell 5.1；打包及其他专项脚本可能另有版本要求。文档检查需要 Node.js。

## 三端当前组合

默认寻找桌面目录相邻的 NPClassworks 与 NPClassworksKV。使用独立工作树时，显式填写三个路径：

```powershell
./scripts/test-npep-current.ps1 `
  -DesktopRoot 'D:\CodeProjects\NPEduTools' `
  -WebRoot 'D:\CodeProjects\NPClassworks' `
  -BackendRoot 'D:\CodeProjects\NPClassworksKV' `
  -Browser -Database
```

需要匹配各仓锁定依赖、Playwright Chromium、原生 PostgreSQL 和 `global.json` 的 .NET SDK。CURRENT 驱动要求 Node 22.18 及以上的 22 系列，或 24 及以上版本，具体由脚本检查。

`-RequireClean` 要求受测源码干净；完整 SHA 参数或来源清单用于准确回放。浏览器和数据库未选时，结果是所选范围的自动检查，不是 `FULL_AUTOMATED`。GitHub 入口解析一次三仓完整 SHA，再对该固定组合检查；旧配对 CI 的固定兼容性组合不能当作当前三端结果。机制见 [CURRENT 任务卡](iterations/NPEP-CURRENT-CI-20261005.md)和 [workflow](../.github/workflows/npep-current.yml)。

CURRENT 结果保存在 `.artifacts/npep-current/runs/<ID>/result.json`，包含阶段日志、源码来源和未执行项。GitHub 中查看对应运行的摘要与 artifact，核对 SHA 和实际阶段，不仅看绿色状态。

## 交付与现场

不传 `-PackageResultPath` 的交付检查使用测试载荷，不能证明最新完整安装包可交付。传入完整候选记录后，仍不代表真实 UAC、OOBE 或录制体验已经通过，详见[交付任务卡](iterations/DESKTOP-DELIVERY-20261004.md)。

自动检查使用隔离服务、合成媒体或系统操作替身，不操作生产学校或真实教室麦克风。现场检查从[大屏验收表](CLASSROOM-ACCEPTANCE.md)及[学校排程现场记录](archive/npep/N4.3d-FIELD-CHECK-20261002.md)选择项目，逐项记下设备、版本、操作和结果。

报告中分别列出：自动检查、人工／设备验收、部署、发布。用户要求跳过的测试保持跳过；旧 `.artifacts` 路径仅是原机器上的证据线索，不保证在新 checkout 存在。带日期的报告保持当轮含义，不能从“单元测试通过”推导生产或现场通过。
