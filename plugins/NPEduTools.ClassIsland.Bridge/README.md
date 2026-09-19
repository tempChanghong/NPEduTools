# NPEduTools ClassIsland 时间桥接（P0）

面向 ClassIsland 2.1.0.1 的只读验证插件。提供有效学校时间、单调采样年龄、当前日程和连接实例身份。

不包含录屏、录音、校时修改、课表修改或远程停止接口。尚未接入 NPEduTools 自动调度。

开发构建：在 NPEduTools 根目录执行 `./scripts/dotnet.ps1 build plugins/NPEduTools.ClassIsland.Bridge -c Release`。

通过 ClassIsland Debug 本体的 `-epp <插件输出目录>` 加载。请优先使用仓库中的隔离联调脚本，避免占用用户本体。

参考：ClassIsland Docs 的插件入口、依赖注入、事件、IPC 和程序集隔离章节。完整设计见仓库 `docs/CLASSISLAND-RECORDING-BRIDGE-PLAN.md`。

代码随 NPEduTools 使用 GPL-3.0；ClassIsland SDK 等依赖遵循各自许可。
