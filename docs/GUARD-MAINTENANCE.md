# NPEduTools 自习守护维护

本版本守护文件为应用目录下的 `Guard\svchost.exe`，文件描述为“Windows 服务主进程”，产品和公司仍为 NPEduTools。它不在 Windows 系统目录，也不是 Microsoft 的服务。维护时以完整路径、进程身份和当前实例记录核对，绝不按 `svchost.exe` 名称批量结束进程。

## 行为和独立启动

已同意协议并提权的 App 启动时，在 Windows 任务计划程序登记当前账户的四个任务：`NPEduTools.Guard.<实例标识>.Guard/Login/App/Host`。名称和登记来源明确标注 NPEduTools。使用交互用户令牌，最高可用权限，实例忽略重复启动，没有执行时限。登录入口动态解析默认通信端点，避免固定旧 Windows 会话编号。

App、Host 和守护通过这些任务启动对方；启动的程序由任务计划程序创建，三者不互为父子进程。App／Host 每五秒检查守护；守护缺失时请求独立恢复。系统每分钟检查一次守护任务，存活时忽略重复启动；即使 App／Host 都已退出，仍能重新启动被结束的待命守护。登录任务只是短暂的启动入口，常驻守护统一由 Guard 任务运行。连续三次 App／Host 恢复失败暂停五分钟，避免故障启动风暴，之后再核验恢复。

| 情况 | 行为 |
|---|---|
| 学校定时自习准备前两分钟到结束边界前 | 按缓存的已确认时段唤醒或恢复 App／Host；Host 重新核验规则、学校时间和授权后才能采集 |
| 非学校定时阶段，App／Host 退出 | 守护继续待命，不恢复手动会话或立即打开主程序 |
| 通过验证停止后台并退出 | 保留本次跳过记录及守护抑制截止时间，下一次自习准备时再启动 |
| Windows 锁屏或休眠 | 不在锁屏桌面启动；休眠撤销当前采集恢复资格，恢复后可按缓存唤醒程序，采集等待新的 Host 核验 |
| 注销或关机 | 暂停恢复，确认会话结束后退出；下一次用户登录重新待命 |
| 显式维护 | 停止守护并撤销所属任务；客户端没有这个入口 |

缓存最多保存最近确认排程的 16 个时段，遵守生效日期、跨午夜、日历例外及本次跳过规则。学校时间与 Windows UTC 同期观察用于投影唤醒时刻，缓存不包含学校身份、凭据、设备选择、音频、照片或采集许可。缓存仅用于启动；离线时无法知道服务器后来修改的配置，启动后必须通过最新授权和学校时间核验，不能把缓存当成离线采集许可。长时间完全退出且缓存时段已耗尽时，需维护人员重新打开 App 刷新计划。

任务登记缺失、权限不足、另一目录占用同一实例或登记内容不匹配时显示失败，不能视为守护已就绪。任务计划程序服务必须可用。Host 以窗口子系统启动，避免系统任务在大屏打开黑色控制台；需要命令行诊断时使用 `dotnet NPEduTools.Host.dll`，重定向的工作进程协议流继续可用。此机制降低随手结束及“结束进程树”的影响，不保证抵御提权后同时结束所有组件或删除任务的管理员。

## 维护、换目录及卸载

1. 正常停止监测／录制并完成管理验证，退出 App／Host。守护此时仍在等待，这是预期行为。
2. 用运行应用的同一 Windows 账户打开管理员终端。到**要维护的实际应用目录**，运行：

   ```powershell
   # 默认实例；自定义测试实例另见下面的命令。
   Start-Process -FilePath '.\Guard\svchost.exe' -ArgumentList '--default-pipe','--maintenance' -WindowStyle Hidden -Wait
   ```

   自定义管道：

   ```powershell
   Start-Process -FilePath '.\Guard\svchost.exe' -ArgumentList '--pipe','NPEduTools.ManualCheck20261011','--maintenance' -WindowStyle Hidden -Wait
   ```

3. 核对本实例 `guard\status.json` 为 `Stopped`、该目录的守护进程已退出、四个所属任务已撤销。默认记录在 `%LocalAppData%\NPEduTools\config\guard`；自定义实例在临时目录中的实例哈希路径。命令不会停止 Windows 服务，不删除配对、报告或自定义排程。任务登记不匹配时保留错误供维护，不删除其他软件的任务。
4. 之后才能覆盖、移动或卸载目录。下次主动打开完整 App 会重新登记该目录的独立启动任务。多账户分别维护；不要用另一个管理员账户的实例记录代替原账户的记录。

必须保留整个 App／Host／Guard 包。仅改名旧 EXE 不能升级；运行文件、DLL、运行时配置及独立启动路径都要来自同一次构建。

机制依据：[Task Scheduler 交互用户安全上下文](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)、[RunEx 指定交互会话](https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask-runex)。
