# Windows 安装包

当前源码版本为 **v1.1.6 · EVA-02（二号机）**；范围与交付边界见[本版说明](releases/v1.1.6.md)。源码更新不会替换已发布的旧版工件，安装包须以其对应发布说明为准。

自 v1.0.0 起提供 Windows x64 安装 EXE，同时保留 ZIP。下载与文件用途以[对应版本说明](releases/README.md)为准；源码更新不会替换旧版附件。

## 使用与升级规则

- 面向 Windows 10／11 x64，默认安装到 `C:\Program Files\NPEduTools`。安装时请求管理员权限；应用启动仍使用原有提权逻辑。
- 安装主程序、Host、Admin、Recorder、自包含 .NET 运行时、FFmpeg／ffprobe、桥接插件包、源码及许可证，不要求用户自行安装 .NET SDK 或录制组件。
- 默认创建开始菜单入口；桌面快捷方式和安装后立即启动均可选，默认不勾选立即启动。后续配置使用应用现有 OOBE。
- ClassIsland／ExamAware2 本体另行安装，插件经它们的插件管理功能导入；安装程序不替用户配对、不自动创建管理员任务、不登记登录启动。
- 同一固定 AppId 用于后续版本覆盖升级。只接受相同或更高的安装版本，损坏的版本登记也会阻止覆盖；回退需先备份并按专门流程处理。
- 发现 NPEduTools App／Host 等组件或安装目录内的 FFmpeg 仍在运行，安装和卸载均拒绝继续；先停止并保存录制／监测，再在托盘选择“停止后台并退出”。WMI 检查失败时同样停止，不强杀采集或录制进程。
- 本地新版安装器自动维护本安装的待命守护：先核验完整路径、任务来源、账户、管道与参数，再记录 `Maintenance`、撤销所属任务并等待守护自行退出（最多十秒）。不按 `svchost.exe` 名称结束进程，不修改其他安装或 Windows 服务。助手由安装器携带，无需旧守护文件存在；卸载使用已安装助手，并核对构建时的 SHA-256。
- 在用户确认卸载后才执行维护；取消／失败时恢复原任务及停止记录，并只重新启动维护前正在运行的守护，不启动 App／Host。完整 `app` 文件集发生变化时不自动启动混用的组件，保留诊断供修复。升级成功后由下次 App 启动重新登记守护，卸载成功后不重新登记。
- 维护标记为 `app\guard-maintenance.json`；新 App 不会在此标记存在时改写守护代次或登记／启动任务。安装期间仍应保持 App／Host 退出；旧版本不识别此标记，安装器会在维护后再次检查进程及任务。此保护不宣称能阻止外部管理员在文件写入阶段强行启动旧版本。

## 用户数据与卸载

计划、配对和设置继续保存在原有 `%LocalAppData%\NPEduTools`；视频仍在用户选择的目录。升级与卸载不清理这些位置，不修改 ClassIsland／ExamAware2 的启动项，也不卸载它们管理的插件。

卸载按 Inno 的安装记录移除已安装文件，不递归删除整个安装目录；目录中未被安装程序登记的文件保留。升级不主动清理旧版本遗留文件，因此包内严格校验脚本主要用于**安装前的解压包**，不能直接将包含卸载器等额外文件的安装目录视为原始 ZIP。

卸载前在应用设置关闭 NPEduTools 登录启动。卸载器会检查已加载 Windows 用户的 Run 登记，发现仍指向此安装位置时阻止卸载，并提示在对应账户关闭。未加载的用户配置不能保证检查到，多账户环境应逐个核对。安装器不直接写其他用户的启动项。

自动守护维护会按任务登记的账户定位默认实例记录；自定义管道需使用所属账户运行维护。事务记录保存在 `%ProgramData%\NPEduTools\installer-maintenance\<ID>`，限制管理员／SYSTEM 访问，包含任务定义、停止记录和文件哈希，不包含学校口令、照片或音频。安装器异常终止时保留标记及记录，先按 [维护说明](GUARD-MAINTENANCE.md)恢复或完成该事务，不删除用户配置来解除阻塞。公开 v1.1.1 的已有安装器不含此本地修改。

ZIP 迁移到安装版时：先在旧程序关闭登录启动 → 保存并完整退出 → 安装到新的固定目录 → 从新入口打开 → 如有需要，重新登记登录启动。用户配置通常沿用同一账户的数据；不要覆盖旧 ZIP，也不要同时运行两份程序。若 UAC 使用的是另一个管理员账户，实际运行账户及其数据目录可能不同，需要核对，安装器不自动迁移账户数据。

## 维护者构建

安装包复用 `package-npedutools.ps1` 生成的完整包，不能直接打包 Debug 目录。需要 Inno Setup **7.1.0 或更新版本**：完整候选包的第三方许可路径可超过 260 字符，实际构建曾发现 Inno 6 的路径限制，因此使用支持长路径的 7。脚本在预处理阶段检查真实编译器版本，不能依赖官方 ISCC 的 `0.0.0.0` 文件版本资源。

已有编译器可传 `-IsccPath`。也可运行：

```powershell
./scripts/bootstrap-inno-setup.ps1
```

这会校验固定官方下载的 SHA-256 与发布者签名，并把编译器安装到仓库 `.tools/inno-setup7`。它是一次**当前用户的开发工具安装**，会产生 Inno Setup 自己的卸载登记，不安装 NPEduTools。工具可通过 Windows 应用列表卸载。

统一入口，同时生成 ZIP、安装 EXE、哈希及构建记录：

```powershell
# 将 NEXT-VERSION 和发版文档替换为实际尚未发布的版本。
./scripts/package-npedutools.ps1 -ReleaseVersion NEXT-VERSION `
  -ReleaseNotesPath docs/releases/NEXT-INSTALLER.md `
  -Installer -InstallerVersion 0.2026.1003.0
```

默认输出到 `.artifacts/releases`。`-InstallerVersion` 是单独用于升级比较的四段数字，每段 0..65535，必须递增。开发日期版本可采用 `0.年.月日.修订`，如 `0.2026.1003.0`；公开显示版本仍来自 `-ReleaseVersion`。这只是构建约定，不给下次公开发布擅自定版本。

已有**当前候选版本的验证包**时，可只编译安装器，不重建应用：

```powershell
./scripts/package-installer.ps1 -PackageRoot '<完整包目录>' `
  -InstallerVersion 0.2026.1003.0
```

这个入口先校验发布身份、全部文件哈希、自包含运行时、两处录制组件与对应源码、桥接包和安装器许可。拒绝旧的外置 FFmpeg 包、已发布版本、覆盖已有安装 EXE及输出到载荷内部。`-ValidateOnly` 只验证输入。

当前 EXE 未签名；SHA-256 用于完整性核对，不是发布者认证。未来可在 CI 增加 Authenticode 签名，不通过取消 UAC、杀毒白名单或降低系统策略解决提示。

## 验证

下一版本统一入口（详细任务卡：[桌面交付](iterations/DESKTOP-DELIVERY-20261004.md)）：

```powershell
./scripts/test-desktop-delivery.ps1
# 完整构建之后，传 package-npedutools.ps1 返回的构建记录：
./scripts/test-desktop-delivery.ps1 -PackageResultPath '<result.json>'
```

守护维护事务与实际待命进程可单独验证：

```powershell
./scripts/test-guard-maintenance.ps1
# 传入本次构建的自包含 Guard 目录；仅创建随机管道的隔离待命守护。
./scripts/test-guard-maintenance-live.ps1 -GuardBundle '<Guard目录>'
```

第一项使用合成任务和进程检查失败、冲突及回退；第二项使用实际任务计划程序与隔离守护。均不启动业务 App／Host，不访问麦克风、摄像头或学校配置；真实学校账户、UAC 与一体机更新仍需验收。

第一条验证隔离安装器及开发录制组件，结果中 `candidateChecked=false`；第二条额外核对本轮 ZIP、安装 EXE、构建记录和解压载荷是同一份内容，并运行包内两处录制器只读探测及合成媒体处理。结果与各子报告在 `.artifacts/desktop-delivery/<ID>/`。不会安装正式应用或启动真实录制；真实 UAC、OOBE、配对保留和教室验收另列。只有哈希匹配但 ZIP 内容与解压载荷不同，也会拒绝。

需要分别定位问题时，可使用原有入口：

```powershell
./scripts/test-installer-package.ps1
./scripts/test-installer.ps1
```

两者兼容 Windows PowerShell 5.1 和 PowerShell 7。应用完整发布脚本沿用其现有 PowerShell 7 要求。

第一项用不可执行的模拟文件检查完整载荷、缺失许可、篡改文件、冻结版本、输出嵌套和重复输出，并实际编译生产配置的 `.iss`。第二项在随机目录和独立 HKCU 测试登记中，实际执行安装、升级、拒绝降级、运行中阻止升级／卸载、启动登记阻止卸载、保留用户文件以及卸载重装。两项都不启动正式应用、录屏或麦克风，不触碰正式安装登记。测试日志保留在 `.artifacts/installer-*-tests/`。

GitHub 的 `Windows installer checks` 运行同样检查，不上传可误用的模拟安装 EXE。CI 配置提交后才会执行，不能把本地通过写成 CI 已通过。

发版前仍要在隔离 Windows 环境验证真实完整载荷：UAC、OOBE、App／Host／Recorder 就绪、短录回放、配对保留、已安装版覆盖升级与卸载。真实大屏验收继续按 [教室验收清单](CLASSROOM-ACCEPTANCE.md)，不能由模拟安装测试代替。

依据：[Inno Setup AppId](https://jrsoftware.org/ishelp/topic_setup_appid.htm)、[安装前检查事件](https://jrsoftware.org/ishelp/topic_scriptevents.htm)、[应用关闭选项](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)、[官方编译器下载](https://jrsoftware.org/isdl.php)。
