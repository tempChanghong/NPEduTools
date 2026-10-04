# NPEduTools v1.0.0：安装、更新与回退

适用：v1.0.0 EVA-01（初号机）内置录制组件包，Windows x64，ClassIsland API 2.1.0.1／桥接 0.2.0.0，ExamAware2 1.5.2／桥接 0.4.0。已发布的 InDev 20261002 仍按随包原说明安装录制组件。发行附件与源码运行方法见对应包的 `RELEASE-NOTES.md`。开发机检查不等于目标大屏整课验收。自包含包内含 NPEduTools 的 .NET 运行时，不需要另外安装开发 SDK。ClassIsland 和 ExamAware2 本体及其运行环境单独维护，不包含在本包内。

## 第一次使用

1. 完整解压 ZIP 到独立固定目录，例如 `D:\NPEduTools`。不要只取出 EXE，不要直接在压缩软件中运行。移动目录后应重新设置相关快捷方式。
2. 双击 `Start-NPEduTools.cmd`，或 `app/NPEduTools.App.exe`，启动时核实并允许 Windows UAC；本版立即请求管理员权限。在偏好设置中确认 ClassIsland 程序路径。
3. 在 ClassIsland 的“应用设置 → 插件”中安装 `ClassIsland-plugin/NPEduTools.ClassIsland.Bridge.cipx`，确认名称为“NPEduTools 时间与课表桥接”，启用并重启 ClassIsland。无需自行解压插件到用户正在使用的安装目录。
4. 打开 NPEduTools 的“自动录课”，核对学校日期、时间与 ClassIsland 显示一致。插件缺失、时间停止前进或连接异常时，等待修复；软件不会改用 Windows 时间代录。
5. 本版内置 FFmpeg／ffprobe，无需另装组件。到“录制微课”选择屏幕、音源、目录，点击“保存设置”。先试 8 fps、720p，再按课件清晰度决定是否升至 1080p；选对麦克风和系统声音，实际短录并回放确认。旧版未含组件的 ZIP 仍按其自带安装说明处理，不要将旧目录覆盖到新包。
6. 保存周期/指定日期计划，检查 17 项默认排除和冲突。先点击“查看近期安排”核对计划；确认后单独启用自动录制。保存计划、启动软件、打开窗口都不会自动启用采集。
7. 收起窗口后可以用侧边栏控制录制。暂停不延长截止；不再需要当天录制时，选择“今天不再自动录制”。完整退出时等待保存完成。

课程默认按学校课表开始前 2 分钟至结束后至多 5 分钟执行；它是按课表推算，尚未直接订阅自定义“二分钟铃”。固定时段直接使用所设起止时间。重启后自动录制开关保持关闭。

## 文件位置

需要日常／考试模式时，先在“考试看板”保存 ExamAware.exe 位置，通过 ExamAware 官方插件安装功能导入 `ExamAware2-plugin/npedutools-examaware-bridge-0.4.0.ea2x`，按该目录 README 完成配对；在 NPEduTools 的“设置 → 软件连接”配置并核实当前 ClassIsland 程序的管理员登录任务，再做首次检查。Windows UAC 由操作者核实授权。考试模式暂停自动录课并保留计划；本地“同时切换当前运行的软件”仍为可选项，网页远程模式切换则联动运行软件、登录自启动与录课暂停。完整返回前先保存编辑器并结束放映。

**切回日常或退出 ExamAware2 前，先保存并关闭编辑器、结束放映。** 1.5.2 编辑器仍打开时退出可能提前卸载接口，导致无法保存；这是本版暂未解决的已知问题。关闭 NPEduTools 不会替你关闭这两款软件。

| 内容 | 位置 |
| --- | --- |
| 主程序及运行时 | 本包 `app/`，子目录必须一起保留 |
| 内置录制组件 | 两处 `Recorder/Tools/`；对应源码、构建与许可证见 `third-party/ffmpeg/` 与 `FFMPEG-BUNDLED-BUILD.md` |
| ClassIsland 插件 | 本包 `ClassIsland-plugin/`；实际安装位置由 ClassIsland 管理 |
| ExamAware2 插件 | 本包 `ExamAware2-plugin/`；通过官方安装功能导入并配对 |
| 计划、界面与录制设置 | `%LocalAppData%\NPEduTools\ui` |
| 后台配置及真实执行账本 | `%LocalAppData%\NPEduTools\config` |
| 视频 | 所选保存目录；默认是用户“视频”目录下 `NPEduTools` |
| 异常片段 | 所选保存目录下 `.npeedutools-sessions`；实际记录也会给出路径 |
| 源码、依赖与校验 | `NPEduTools-source.zip`、`third-party/`、`build-locks/`、`package-manifest.json` |

本包是程序免安装，并非将个人配置也放在程序旁边的完全便携模式。换电脑时配置不会自动跟随；先重新核对屏幕、音源和保存目录。录课视频、原始麦克风音频与本机录课计划不上传。学校配对即授权当前学校互联能力：会上传设备状态、操作回执与噪音统计，学校可投递通知、考试方案及定时规则。只需本机功能时不必配对学校。

## 更新

1. 停用自动录制，停止并保存当前会话，完整退出 NPEduTools。不要用覆盖文件的方式替换运行中的录制器。
2. 备份整个 `%LocalAppData%\NPEduTools`，保留视频及 `.npeedutools-sessions`。ClassIsland 按其自身方式备份档案与插件配置。
3. 将新 ZIP 解压到旁边的新目录，保留旧程序目录。使用新目录的启动入口；配置仍按当前用户读取。本版已含录制组件，完整解压即可，不必复制旧 FFmpeg。
4. 桥接插件 ID 为 `npedutools.recordingbridge`，更新使用同 ID 的新 `.cipx`，通过 ClassIsland 插件管理完成并重启。核对版本与学校时间。
5. 新版本完成短录与回放后再启用自动录制。如已设置登录启动，到新版本偏好设置重新登记，避免仍指向旧目录。

## 回退或移除

- 先停用自动录制并保存退出，再启动保留的旧程序目录。旧版本可能不识别新配置，遇到未知版本应停止并保留文件，不能删除账本后尝试重录。
- 如必须恢复更新前配置，先另外备份**更新后的完整数据**。优先保留当前真实执行账本；恢复旧账本会丢失更新期间的去重记录。不能保证兼容时先仅使用手动录制，核对记录后再安排自动任务。
- 桥接回退同样通过 ClassIsland 管理器安装保留的旧 `.cipx` 并重启。旧 0.1 插件不提供未来日期查询；不能将降级后缺少的能力视为正常。
- 移除 NPEduTools 前如启用了登录启动，在偏好设置关闭；确认无运行会话后可删除对应程序目录。个人配置和视频不会随此步骤删除。禁用/卸载桥接不会删除 NPEduTools 的视频与计划。

## 校验与故障定位

ZIP 旁的 `.sha256` 校验完整压缩包；包内 `package-manifest.json` 列出各文件哈希。管理员可在 PowerShell 中执行 `Get-FileHash <ZIP路径> -Algorithm SHA256`，或运行本包 `verify-portable-package.ps1` 检查解压文件。本地哈希用于完整性核对，不是数字签名或发布者身份认证。本版尚未做 Authenticode 签名。

包内校验脚本使用 PowerShell 7。本版默认检查两处内置 FFmpeg 的精确哈希、许可证及对应源码归档。历史不含组件的包仍默认拒绝混入 FFmpeg，用户另装后才使用其 `-AllowInstalledRecordingTools` 模式。

启用失败先看时间源、保存路径、音源与可用空间。录制结束显示“中断待检查”时保留原视频和片段，先检查输出，不能通过删账本或重复启用来盲目补录。当前无自动归档/显式重试入口。

录制器恢复片段可由维护人员调用 `app/Recorder/NPEduTools.Recorder.exe --recover "完整会话目录"`；操作前备份该目录并确保没有其他录制进程。不要直接编辑片段清单或执行账本。

## 验收与交付范围

按同包 `CLASSROOM-ACCEPTANCE.md` 填写目标大屏结果。至少完成整课、连续多课、双音源回放、暂停恢复、ClassIsland 断连及物理休眠验证，再决定常态使用。不存在“开发机短录已通过，所以大屏一定不会假死”的结论。

本包附项目源码快照、实际发布依赖清单、许可证与 ClassIsland/ExamAware/FFmpeg/x264 对应源码及构建材料，详见 `THIRD-PARTY-MATERIALS.md`。以 `package-manifest.json` 中的提交号、工作区状态及源码哈希定位构建内容。本版不会替换 GitHub 旧版本附件，以实际下载包的清单为准。
