# 测试与验收

[文档首页](README.md) · [开发指南](DEVELOPMENT.md)

## 选择入口

| 改动范围 | 在仓库根目录运行 | 覆盖范围 |
| --- | --- | --- |
| 桌面常规开发 | `./scripts/verify.ps1` | 锁定还原、Release 构建、解决方案测试 |
| 文档整理 | `node scripts/check-doc-links.mjs` | 本仓库 Markdown 的本地文件目标；远程 URL、标题锚点和外部资料单列 |
| 三端整体回归 | `./scripts/test-npep-current.ps1 -Browser -Database` | 当前三仓组合、协议、桌面、网页、临时数据库与 HTTP 检查 |
| 定时监测与保护 | `./scripts/test-npep-noise-schedules.ps1 -Guard -Display -Protection -Presence` | 定时监测、显示、管理验证、在线状态和守护的专项自动检查；可加 `-Browser -Database` |
| 噪音状态界面 | [构建并运行隔离 WPF 检查](iterations/NOISE-STATUS-PRESENTATION-20261007.md#验证) | 当前／历史统计、断连与恢复、最小尺寸布局；不启动真实采集 |
| 自动录课状态界面 | [构建并运行隔离 WPF 检查](iterations/AUTOMATIC-RECORDING-STATUS-20261007.md#验证) | 设置验证、实际状态与操作反馈、记录和最小尺寸布局；不执行真实录课命令 |
| 手动录课恢复 | [客户端与隔离 WPF 检查](iterations/MANUAL-RECORDING-RECOVERY-20261007.md#验证) | 断连、重新确认、旧查询时序与回执丢失；使用唯一模拟管道，不启动真实录制 |
| 点名回执恢复 | [SecRandom 专项与隔离 WPF 检查](iterations/SECRANDOM-STATE-RECOVERY-20261007.md#验证) | 页面与侧栏失联、旧查询时序、历史回执和恢复；不执行真实抽取 |
| 考试看板恢复 | [ExamAware 专项与隔离 WPF 检查](iterations/EXAMAWARE-STATE-RECOVERY-20261007.md#验证) | 方案与操作请求时序、失联恢复及配对导出失败；不操作真实软件或自启动 |
| 本机课堂模式恢复 | [课堂模式专项与隔离 WPF 检查](iterations/CLASSROOM-STATE-RECOVERY-20261007.md#验证) | 断连、新旧查询顺序与过期配置检查；只读模拟请求，不切换真实软件或自启动 |
| 定时监测返回回执 | [展示专项与隔离 WPF 检查](iterations/SCHEDULED-DISPLAY-RETURN-20261007.md#验证) | 原生备用页面的新旧会话、返回回执与失败重试；不启动采集或连接学校 |
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
