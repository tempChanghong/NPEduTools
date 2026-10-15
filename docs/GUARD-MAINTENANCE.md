# NPEduTools 自习守护维护

本版本守护位于**主程序所在目录**的 `Guard\svchost.exe`。完整发行包的主程序位于安装根目录的 `app` 子目录；安装版默认守护路径因此是 `C:\Program Files\NPEduTools\app\Guard\svchost.exe`，不是 `C:\Program Files\NPEduTools\Guard\svchost.exe`。文件描述为“Windows 服务主进程”，产品和公司仍为 NPEduTools。它不在 Windows 系统目录，也不是 Microsoft 的服务。维护时以完整路径、进程身份和当前实例记录核对，绝不按 `svchost.exe` 名称批量结束进程。

## 行为和独立启动

已同意协议并提权的 App 启动时，在 Windows 任务计划程序登记当前账户的四个任务：`NPEduTools.Guard.<实例标识>.Guard/Login/App/Host`。名称和登记来源明确标注 NPEduTools。使用交互用户令牌，最高可用权限，实例忽略重复启动，没有执行时限。登录入口动态解析默认通信端点，避免固定旧 Windows 会话编号。

App、Host 和守护通过这些任务启动对方；启动的程序由任务计划程序创建，三者不互为父子进程。App／Host 每五秒检查守护；守护缺失时请求独立恢复。系统每分钟检查一次守护任务，存活时忽略重复启动；即使 App／Host 都已退出，仍能重新启动被结束的待命守护。登录任务只是短暂的启动入口，常驻守护统一由 Guard 任务运行。连续三次 App／Host 恢复失败暂停五分钟，避免故障启动风暴，之后再核验恢复。

| 情况 | 行为 |
|---|---|
| 学校定时自习开始前五分钟到结束边界前 | 按缓存的已确认时段唤醒或恢复 App／Host；Host 重新核验规则、学校时间和授权后才能采集 |
| 非学校定时阶段，App／Host 退出 | 守护继续待命，不恢复手动会话或立即打开主程序 |
| 通过验证停止后台并退出 | 保留本次跳过记录及守护抑制截止时间，下一次自习准备时再启动 |
| Windows 锁屏或休眠 | 不在锁屏桌面启动；休眠撤销当前采集恢复资格，恢复后可按缓存唤醒程序，采集等待新的 Host 核验 |
| 注销或关机 | 暂停恢复，确认会话结束后退出；下一次用户登录重新待命 |
| 显式维护 | 停止守护并撤销所属任务；客户端没有这个入口 |

缓存最多保存最近确认排程的 16 个时段，遵守生效日期、跨午夜、日历例外及本次跳过规则。学校时间与 Windows UTC 同期观察用于投影唤醒时刻，缓存不包含学校身份、凭据、设备选择、音频、照片或采集许可。缓存仅用于启动；离线时无法知道服务器后来修改的配置，启动后必须通过最新授权和学校时间核验，不能把缓存当成离线采集许可。长时间完全退出且缓存时段已耗尽时，需维护人员重新打开 App 刷新计划。

任务登记缺失、权限不足、另一目录占用同一实例或登记内容不匹配时显示失败，不能视为守护已就绪。任务计划程序服务必须可用。Host 以窗口子系统启动，避免系统任务在大屏打开黑色控制台；需要命令行诊断时使用 `dotnet NPEduTools.Host.dll`，重定向的工作进程协议流继续可用。此机制降低随手结束及“结束进程树”的影响，不保证抵御提权后同时结束所有组件或删除任务的管理员。

## 维护、换目录及卸载

v1.1.2 安装器自动执行本安装的守护维护，正常升级／卸载无需手工运行以下命令。v1.1.1 已有安装器不包含这项修改；ZIP 换目录和旧安装器仍可使用手工步骤。

自动维护先检查全部任务的来源、账户、参数和操作路径，持久化代次对应的 `Maintenance` 后撤销任务，最多等待十秒，超时不强杀进程。它可清理旧守护文件已缺失、但仍有正确登记的安装；归属不明或登记被修改时保留错误，不删除其他任务。默认实例按任务的账户配置路径定位，自定义管道仅由所属账户维护。

初始恢复记录先保存，再创建会阻止组件启动的维护标记。若在首次保存前意外中断，不会遗留无记录的启动阻塞；标记生效后，所属事务已有恢复记录。已有标记冲突时保留原标记，不把本次未接管的记录留作待恢复事务。

同一账户、同一目录的不同通信实例分别核对进程调用参数，不能仅凭账户相同就认定都在运行。取消或失败安装时只恢复原来实际运行的常驻守护；登录引导、维护命令及闲置实例不会因此被额外启动。无法读取进程参数，或常驻进程不能匹配所属任务组时，保留错误并停止自动维护。

默认 Host 在没有额外互联参数时允许空参数；Windows 任务计划程序保存任务后可能省略空的 `Arguments` 节点，维护时与空字符串等价。其他角色的必要参数仍须完整匹配，不能省略。

安装失败／取消会在原 `app` 文件完整时恢复任务定义和原停止记录，仅重新启动维护前正在运行的守护。升级成功后等下次 App 登记，卸载成功后不恢复。运行中的 App／Host／录制仍须先正常退出。

原文件快照和恢复前的文件清单核验包含 `app` 目录中的隐藏文件及隐藏目录内容，文件属性不会使组件跳过完整性检查。组件被修改，或文件清单发生增减时，保留维护记录并拒绝恢复，避免继续使用不完整的安装。

准备维护和恢复任务仍要求任务计划程序可用。安装／卸载完成后的本地事务收尾，以及已完成恢复后遗留标记的清理，不再连接任务计划程序；服务临时不可用不会额外阻塞这些清理步骤。事务归属、记录写盘和标记删除仍须成功，其他事务的标记不会被删除。

恢复过程中若任务计划程序未能接受守护启动请求，会保留原维护标记和事务记录；排除故障后可用同一事务重试 `Restore`。标记在所有恢复启动请求均已接受后解除，重试仍核对文件、任务归属和实例代次。

恢复完成状态须先保存，再清理维护标记。完成记录写入失败时保留原事务；若只在标记清理时中断，再次 `Restore` 只清理属于原事务的标记，不重复登记或启动守护。

若安装器异常终止，`app\guard-maintenance.json` 指向持久事务 `JournalPath`；不要直接删除标记来强行启动。使用同一版本的完整维护助手并保持 App／Host 退出，通过管理员 PowerShell 执行下列命令，路径需替换为该事务的实际值：

```powershell
# 仅当原 app 文件仍完整：恢复旧任务和守护；文件已变更时助手拒绝自动恢复。
& 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File 'C:\Program Files\NPEduTools\maintenance\guard-maintenance.ps1' `
  -Action Restore -InstallRoot 'C:\Program Files\NPEduTools' `
  -TransactionDirectory '<标记指向的事务目录>'
```

如果文件已更换，先修复／核验完整同版本安装包，再由维护人员使用 `-Action Complete` 解除对应事务，不尝试恢复旧进程。事务记录位于 `%ProgramData%\NPEduTools\installer-maintenance`，受管理员／SYSTEM 权限保护；包含任务 XML、停止记录和文件哈希，不包含学校凭据、照片或音频。维护助手、注册代次或停止记录不匹配时不覆盖。自定义管道恢复必须由原账户执行。此手工入口不在用户端 UI 中。

维护失败时，安装器显示助手的启动／执行错误码及可读取的具体原因。请保留弹窗附带的诊断目录：`Prepare-result.txt`、`Restore-result.txt`、`Complete-result.txt` 分别记录各步骤结果，`result.txt` 仅代表最后执行的步骤。准备失败后若自动恢复成功，不能把最后的 `OK` 当作准备成功；应查看 `Prepare-result.txt`。旧版助手只有 `result.txt`，可能在恢复后覆盖首次错误。若目录或结果文件均未生成，请一并提供安装器日志，可能是助手尚未成功启动。

1. 正常停止监测／录制并完成管理验证，退出 App／Host。守护此时仍在等待，这是预期行为。
2. 用运行应用的同一 Windows 账户打开管理员 PowerShell。安装版使用下列默认路径；自定义安装或 ZIP 版把 `$guardExe` 改为要维护的那一份完整包中 `app\Guard\svchost.exe` 的实际完整路径：

   ```powershell
   # 默认实例；路径含空格必须保持引号，无需先 cd。
   $guardExe = 'C:\Program Files\NPEduTools\app\Guard\svchost.exe'
   if (-not (Test-Path -LiteralPath $guardExe -PathType Leaf)) {
       throw '此路径没有守护文件。请核对实际安装目录，不要删除配置或按进程名称批量停止。'
   }
   $maintenance = Start-Process -FilePath $guardExe -ArgumentList '--default-pipe','--maintenance' -WindowStyle Hidden -Wait -PassThru
   if ($maintenance.ExitCode -ne 0) { throw ('维护命令失败，退出码：' + $maintenance.ExitCode) }
   ```

   自定义管道（同样先把 `$guardExe` 指向对应完整包）：

   ```powershell
   Start-Process -FilePath $guardExe -ArgumentList '--pipe','NPEduTools.ManualCheck20261011','--maintenance' -WindowStyle Hidden -Wait
   ```

3. 命令通常没有弹窗；维护入口退出码 `1` 表示参数、实例登记或安装目录未通过核验，`2` 表示写停止记录或撤销任务失败，`0` 表示这两步已成功。缺失／损坏登记不会再被当作成功返回；旧版守护可能在这些情况下也返回 `0`。维护入口不等待原守护进程完成退出，因此仍须核对本实例 `guard\stop.json` 的 `Generation` 与 `registration.json` 一致、`Reason` 为 `Maintenance`，运行中的守护停止后 `status.json` 为 `Stopped`，该目录的守护进程已退出、四个所属任务已撤销。默认记录在 `%LocalAppData%\NPEduTools\config\guard`；自定义实例在临时目录中的实例哈希路径。若守护原本未运行，状态文件可能未刷新，以匹配的维护停止记录及所属任务已撤销核查，不要求陈旧状态文件自行变化。命令不会停止 Windows 服务，不删除配对、报告或自定义排程。任务登记不匹配时保留错误供维护，不删除其他软件的任务。
4. 之后才能覆盖、移动或卸载目录。下次主动打开完整 App 会重新登记该目录的独立启动任务。多账户分别维护；不要用另一个管理员账户的实例记录代替原账户的记录。

必须保留整个 App／Host／Guard 包。仅改名旧 EXE 不能升级；运行文件、DLL、运行时配置及独立启动路径都要来自同一次构建。

若正确的 `app\Guard\svchost.exe` 也不存在，先检查安装目录和任务操作中的完整程序路径；这属于文件缺失或目录不符，不能按“维护成功”继续。不要把下载 ZIP 中另一目录的守护直接运行来撤销旧实例：维护入口会核对自身主程序目录与原登记目录，目录不匹配时不会撤销。此时保留安装目录、四个任务的名称和操作路径及维护记录供排查。

机制依据：[Task Scheduler 交互用户安全上下文](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)、[RunEx 指定交互会话](https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask-runex)。
