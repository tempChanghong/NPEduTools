# NPEduTools 开发指南

普通用户直接从[发布页](https://github.com/tempChanghong/NPEduTools/releases)下载 Windows 包即可，无需执行下列步骤。

## 在 Visual Studio 中运行

1. 使用支持项目所需 .NET SDK 的 Visual Studio，安装“.NET 桌面开发”工作负载。
2. 安装根目录 `global.json` 所指定的 SDK：当前基线为 **10.0.400**，允许同系列最新补丁。以仓库配置为准，不沿用 ClassIsland 本体的 SDK 选择。
3. 打开仓库根目录 `NPEduTools.sln`，等待 NuGet 依赖还原。需要管理员环境时以管理员身份运行 IDE；主应用普通启动时也会请求提权。
4. 将 **NPEduTools.App** 设为启动项目，选择 **Debug**，按 **F5** 构建并运行。
5. ClassIsland、ExamAware2 和学校互联属于可选联调对象，分别配置真实程序路径、插件和本地服务。涉及软件切换、麦克风或录屏的操作会产生实际效果，先使用测试配置。

App 构建会复制所需的 Host、Guard、Admin 和 Recorder 目录。不要单独移动 App EXE；录课还需准备录制组件，具体锁定版本与构建材料见[下一版录制组件说明](releases/NEXT-FFMPEG.md)。

调试受保护的定时监测前，先在托盘正常停止后台并退出（需要本机管理验证）。只关闭窗口会继续采集，直接结束 App／Host 可能触发 Guard 恢复。Guard 只保护实际定时采集，不保护手动监测，也不处理存活但卡住的进程；见[守护任务卡](iterations/SCHEDULED-NOISE-GUARD-20261004.md)。

## 命令行检查

在仓库根目录运行：

```powershell
./scripts/verify.ps1
```

这个入口执行锁定依赖还原、Release 构建及解决方案测试。已存在 Release 构建时，可通过 `./scripts/start-app.ps1` 打开主程序；它本身不负责构建。

验证脚本有各自的条件和范围，单元测试通过不等于真实教室大屏、麦克风、触摸或生产服务验收。跨仓库联调使用隔离的本地服务和原生 PostgreSQL，不在生产环境探索调试。

定时监测 P1～P3 使用同一个 PowerShell 5.1 入口：

```powershell
.\scripts\test-npep-noise-schedules.ps1 -Guard -Display -Protection -Presence
# 已安装原生 PostgreSQL 和项目的 Playwright Chromium 时，可加 -Database -Browser。
```

每轮在 `.artifacts/noise-schedule-tests/run-<随机ID>/` 保存 `result.json` 和分项日志。结果只覆盖所选开关；没有选择数据库／浏览器检查，不能把它们记为通过。数据库检查创建并清理临时集群，浏览器使用隔离测试服务，不调用真实麦克风。

学校设备运维概览与故障诊断摘要使用统一入口（PowerShell 5.1／7）：

```powershell
.\scripts\test-npep-operations.ps1 -CheckOnly
.\scripts\test-npep-operations.ps1 -Browser -Database
```

不带可选开关时执行相关单元／生命周期回归、真实 .NET 合成数据的跨端检查与隔离 WPF 编译。报告记录三仓源码基线和实际执行证据，未选择项不计通过，工作区改动不当作已提交发布版本。说明与结果见[运维验收任务卡](iterations/NPEP-OPERATIONS-ACCEPTANCE-20261004.md)。

最近的 CI、诊断对照与完整传输联调改动，先看[运维审核索引](iterations/NPEP-OPERATIONS-REVIEW-20261004.md)，其中列明代码入口、隔离复验来源和仍待现场验收的项目。

## 继续阅读

- [架构说明](ARCHITECTURE.md)：App、Host、CLI、隔离工作进程与 ClassIsland IPC；包含历史实现记录。
- [本地 ClassIsland 联调资料](LOCAL-DEVELOPMENT.md)：记录原开发环境，目录和 SDK 请按当前工作区与项目配置核对。
- [学校互联文档](npep/README.md)：通知、考试环境、噪音监测及相关迭代入口。
- [自动录课页面说明](iterations/AUTO-RECORDING-PAGE-20261003.md)：当前页面分区及使用流程。
- [Windows 安装包](INSTALLER.md)：Inno Setup 依赖、统一打包与隔离安装测试。
- [原 README 开发记录](archive/README-DEVELOPMENT-HISTORY-20261003.md)：保留旧阶段、命令和证据，仅作历史参考。

开发分支、代码合并、Windows 打包、网站部署和现场验收是不同步骤；不要以任一项的完成代替其他项。
