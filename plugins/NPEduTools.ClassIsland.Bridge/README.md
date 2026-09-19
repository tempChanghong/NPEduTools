# NPEduTools ClassIsland 时间与课表桥接 0.2.0.0

面向已验证的 Windows ClassIsland 2.1.0.1。提供有效学校时间、单调采样年龄、当前日程和连接实例身份，以及学校今天起 31 天（今天 + 未来 30 天）的预计课表。

NPEduTools 已用这些数据进行周期/指定日期计划预演及实际自动录制；录制由 NPEduTools 用户单独启用。插件自身不包含录屏、录音、校时修改、课表修改或远程停止接口。

安装：在 ClassIsland 的插件管理中选择配套 `.cipx` 文件，启用后重启 ClassIsland；更新 0.1 版本时使用相同插件 ID。NPEduTools 的新日期规划需要本版本的 `calendar-31-days` 能力。旧版本仍可提供今日学校时钟，未来日期查询会提示更新。

未来日期只表示按当前档案推算的预计结果。NPEduTools 到当天重新读取生效日程；浏览未来日期不会替换实时预演的数据。日期查询只读，最多排队 4 项，单次等待上限 1.5 秒；繁忙或卡顿时返回失败，可稍后刷新。

禁用或卸载后，新预演及新自动录制等待时间源恢复，不切换到系统时间；已开始的录制受原截止约束。规则与记录保存在 NPEduTools 中，插件卸载不会删除它们。当前兼容验证仅覆盖上述开发版本；正式发行本体、管理员权限组合和大屏现场验收仍需单独完成。

开发构建：在 NPEduTools 根目录执行 `./scripts/dotnet.ps1 build plugins/NPEduTools.ClassIsland.Bridge -c Release`。

通过 ClassIsland Debug 本体的 `-epp <插件输出目录>` 加载。请优先使用仓库中的隔离联调脚本，避免占用用户本体。

参考：ClassIsland Docs 的插件入口、依赖注入、事件、IPC 和程序集隔离章节。完整设计见仓库 `docs/CLASSISLAND-RECORDING-BRIDGE-PLAN.md`，本次实现与证据见 `docs/AUTO-RECORDING-PLANS.md`。

代码随 NPEduTools 使用 GPL-3.0；ClassIsland SDK 等依赖遵循各自许可。
