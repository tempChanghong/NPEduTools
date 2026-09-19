# P4 配套试用包与开发机安装验证

日期：2026-09-19。承接 [P3 真实自动录制](AUTO-RECORDING-EXECUTION.md)。**本轮完成配套便携包、解压运行验证、真实 ClassIsland 桥接与自动短录联调；教室大屏整课验收仍未完成。**

## 1. 本轮交付

- [Windows x64 配套 ZIP](../.artifacts/releases/NPEduTools-P4-preview-20260919-201525-814c7bf0-win-x64.zip)
- [ZIP SHA-256](../.artifacts/releases/NPEduTools-P4-preview-20260919-201525-814c7bf0-win-x64.zip.sha256)
- [安装、更新与回退说明](PORTABLE-CLASSROOM-GUIDE.md)
- [大屏验收表](CLASSROOM-ACCEPTANCE.md)

包 ID：`NPEduTools-P4-preview-20260919-201525-814c7bf0`。

ZIP 为 412,488,250 字节，约 393.4 MiB；解压内容约 959.6 MiB，建议为程序保留至少 2 GB 空间，录制视频容量另计。程序含运行时，无需另外安装 NPEduTools 开发 SDK；ClassIsland 本体未捆绑。

ZIP SHA-256：

```text
E682CC18E7837CCD6035D96CE310DD7C2A008421793193522E1B7D635C408C33
```

## 2. 打包构成及解决的问题

此前 App、Host、Recorder 使用 Build 后复制附带文件的方式，本地构建可运行，却不能据此认定 `dotnet publish` 会包含所有后台、录制工具和运行时。本轮新增 `scripts/package-npedutools.ps1`：

1. 验证原始锁定依赖和 FFmpeg 固定哈希。
2. 单独发布 App、Host、管理员辅助和 Recorder 为 Windows x64 自包含程序，不裁剪、不合并为单文件。发布使用独立锁文件目录，保留实际发布依赖；串行构建避免 Recorder 经两个项目引用路径同时写入输出。
3. 按运行时实际查找位置组装 `app/Host`、`app/Admin`、`app/Recorder`、`app/Host/Recorder`，补齐 FFmpeg/ffprobe、上游许可及校验记录。
4. 调用 ClassIsland PluginSdk 的 `CreateCipx` 流程生成 0.2.0.0 插件；不附加测试插件或宿主拥有的另一套 IPC/Avalonia 程序集。
5. 加入启动入口、安装指南、现场验收表、项目源码快照、21 个发布依赖的元数据及可取得的许可文件、发布锁文件。
6. 为 1532 个包内文件计算哈希；先通过结构/依赖检查，再生成 ZIP 及外部 SHA-256。

发布运行时为 .NET / Windows Desktop 10.0.11，SDK 为 10.0.400。桥接插件仍面向 net8.0，依赖 ClassIsland 宿主运行，不捆入第二套 .NET 8 本体。

项目源码快照以提交 `221396f129c6e1e967e2288628849055f10dc06b` 为基线，**包含当时未提交的工作区实现**，不能仅凭提交号重现此包。实际源码 ZIP SHA-256：

```text
6887A9EC44515AACBC464FF59672D370BD5D6D667423BF9B6CEB90C7CB86722B
```

包生成后只改进了外部验收脚本的等待条件并补充报告，没有替换包内二进制。包内源码是打包时快照，后续报告和测试脚本以当前仓库版本为准。

## 3. 本次实际验证

开发机：Windows 11 家庭版 64 位，版本 10.0.26200，Intel Core Ultra 9 275HX。它不是用户指定的 i7-1065G7 大屏。

先将交付 ZIP 解压到新的 `.artifacts/portable-package-test/` 目录；后续启动程序均明确指向该解压目录，未用原 App 构建输出替代。ClassIsland 使用用户已有 2.1.0.1 开发构建的隔离副本，独立设置/档案和测试插件，不修改用户日常配置。

| 项目 | 结果与边界 |
| --- | --- |
| 包内文件、运行时和工具哈希 | 1532 个文件通过；FFmpeg 固定哈希吻合，必需 EXE/CoreCLR/hostfxr/hostpolicy 均在位 |
| 完整性负向检查 | 在隔离解压目录修改 README 后校验拒绝；恢复原字节后重新通过 |
| 自包含运行 | 实际自动录制期间读取 App、Host、Recorder 已加载模块，三者 `coreclr.dll` 均来自解压目录；没有用开发 SDK 目录的运行时 |
| 管理员辅助启动 | 包内辅助程序可启动，非法/缺失参数以约定退出码 2 拒绝；此检查没有请求提权，也不替代管理员运行组合验收 |
| 实际 `.cipx` 加载 | 将 ZIP 中 `.cipx` 解压到隔离 ClassIsland 标准 `data/Plugins/npedutools.recordingbridge`，不用 `-epp` 开发目录加载；握手、版本与日程正常 |
| 真实本体 → 插件 → 新包 → 实际视频 | WPF 保存设置、固定学校日期自动开始、侧边栏暂停、原截止保存、实际记录、学校当天暂停均通过，MP4 完整解码通过 |
| 手动录制回归 | 新解压 App 的开始、隐藏、暂停/继续、停止保存、退出前保存通过；两段 MP4 完整解码通过 |
| 桥接生命周期及日期回归 | 共 29 项检查通过（包含新包自动短录）；覆盖真实本体的未来日期查询、±120 秒偏移、UI 阻塞、跨午夜、无课、退出、重启、强制结束与插件缺失 |

本轮实际录制只采集测试创建的绘图窗口，关闭系统声音和麦克风。真实本体短录打通执行链；声音、整课资源占用和大屏低负载稳定性仍须现场验证。

第一次联调的测试断言过早：UI 收到录制器 Saved 时，Host 账本可能在随后约 250 ms 的调度周期才更新为 Recorded。已将脚本改为有界等待账本确认，不用“UI 显示已保存”替代持久化结果；修正后真实链路通过。产品录制逻辑本轮未修改。

本次验证的是解压后的标准插件目录加载，**没有自动操作 ClassIsland 插件管理界面的安装按钮**，因此安装器交互、升级/降级弹窗和正式发行本体兼容性仍需现场核对。

## 4. 可复核证据

| 证据 | 路径 |
| --- | --- |
| 打包日志 | `.artifacts/package-npedutools-run.log` |
| 包定位与 SHA-256 | `.artifacts/releases/NPEduTools-P4-preview-20260919-201525-814c7bf0/result.json` |
| 解压测试目录 | `.artifacts/portable-package-test-current.json` |
| 修改文件拒绝与恢复校验 | `.artifacts/portable-package-integrity.json` |
| 真实 ClassIsland 与新包联调 | `.artifacts/classisland-bridge/ad59abda8ad04822ad4c1b1af62ce4af/summary.json`，同目录保留响应及进程日志 |
| 新包真实自动录制 UI | `.artifacts/automatic-recording-ui/4d273fb28ded4dad9156f8181bcd1fa0/summary.json`，含视频与截图 |
| 新包手动录制 UI | `.artifacts/recording-ui/4b20c658491d42cbbfcf37c65222be57/summary.json`，含视频与截图 |

本轮修改范围为打包/验证脚本和文档，没有新增产品行为。P3 的 258 项测试结果见 [P3 报告](AUTO-RECORDING-EXECUTION.md)；不把历史测试说成本轮重新运行的结果。

复现：

```powershell
./scripts/package-npedutools.ps1
./scripts/verify-portable-package.ps1 -PackageRoot "完整解压包目录"
./scripts/test-recording-ui.ps1 -AppDirectory "完整解压包目录/app"
./scripts/test-classisland-bridge.ps1 -VerifyHost `
  -BridgePackage "完整解压包目录/ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx" `
  -PortableAppDirectory "完整解压包目录/app"
```

联调脚本需要本仓库已构建的测试设施和本地 ClassIsland 开发构建；教室日常运行不需要这些。若有用户本体正在运行，隔离联调脚本拒绝启动，不强行关闭本体。实际采集测试应串行运行。

## 5. 交付边界与下一阶段

本包只作本地教室试用交付，未上传市场、未创建发行版，也未做 Authenticode 签名。包含项目源码、NuGet 元数据及可取得的许可说明；FFmpeg/全部第三方构建依赖的完整对应源码材料仍需在对外正式分发前整理，不将本次材料汇总声称为已经完成正式发行审查。

后续由现场按 [验收表](CLASSROOM-ACCEPTANCE.md) 执行：i7-1065G7 核显大屏整课、连续至少三节、实际双音源、PPT/白板/视频切换、物理睡眠恢复、普通/管理员权限组合、实际插件管理器安装/更新及回退。也应在无开发环境的目标机检查启动。

不能远程取得目标大屏的性能、声音和休眠结果时，这些项目保持“待现场验证”，不虚填通过。默认自动录制开关关闭，用户核对学校时间、录制设置与计划后再启用。
