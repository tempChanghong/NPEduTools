# 桌面配置与进程恢复

[迭代索引](README.md) · [测试入口](../TESTING.md)

首轮目标：修复三个已经复现的异常处理遗漏。基线为 `f0d39cf`（PR #12 合并后的 main），仅本地提交。

| 问题 | 修复前 | 修复后 |
| --- | --- | --- |
| ExamAware 配置为 `null`、缺字段或版本不支持 | 内容校验抛出异常，Host 构造中断 | 桥接显示不可用，拒绝配对；原文件保留 |
| 课堂模式记录内容无效或考试状态未暂停录课 | 内容校验抛出异常，Host 构造中断 | 模式显示不可用，自动录课保持暂停；原文件保留 |
| 录制器返回 `null` 或超过 16384 字符的状态行 | 读取任务失败，异常再次从清理过程抛出 | 启动失败有明确状态，清理可完成 |

三个执行边界都漏接了 `InvalidDataException`；它不属于 `IOException`。生产代码仅在对应 catch 中补上该类型，不更改数据格式、覆盖损坏文件或自动重试录制。

## 复现与验证

- 修复前：11 个针对场景中 8 个失败，3 个普通坏 JSON 对照通过。
- 修复后：111 项相关回归通过，0 跳过；包含两个独立测试 Host 的启动、模块状态、`host.ping` 和原文件保留检查。
- 录制器使用测试进程输出合成状态；未调用真实录制器或音频设备。
- Release 串行构建：0 警告、0 错误。
- 全量测试：822 项桌面测试 + 183 项 NPEP 测试，共 1005 项通过，0 失败、0 跳过。
- 文档检查：199 份 Markdown、931 个本地文件链接通过；差异检查通过。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --no-restore --configuration Release '-m:1' --filter 'FullyQualifiedName~ExamAwareTests|FullyQualifiedName~ClassroomModeTests|FullyQualifiedName~RecorderProcessTests|FullyQualifiedName~RecordingExecutionTests|FullyQualifiedName~HostReadinessTests'
./scripts/dotnet.ps1 build NPEduTools.sln --no-restore --configuration Release '-m:1'
./scripts/dotnet.ps1 test NPEduTools.sln --no-build --no-restore --configuration Release
```

本地证据：`.artifacts/overnight-20261008/` 下的 before、after、integration、full 测试报告。真实大屏、录课设备、部署和发行未测试。

## 后续：录制器启动握手

基线 `717116e`。Host 原先在收到任何可解析的 JSON 后都放行启动，即使它是空对象、缺少提示文字、未知状态、带会话归属的 Idle，或旧 Saved 回执。这些内容不能证明新组件已准备好接收启动命令，可能让页面停在“正在准备录制”。

现在首条状态必须是未占用的 Idle，或未占用且明确报告的 Failed，并带有提示文字；否则启动失败并正常清理进程。正常 Idle 后的启动／停止命令、会话匹配和组件原始错误仍按原流程处理，不修改协议或录制设置。

- 修复前：10 项测试中，5 个无效握手用例失败（启动错误地返回成功），5 个对照通过。
- 修复后：19 项录制进程、执行记录和 Host 就绪检查通过，0 跳过；测试进程仅收发合成状态，不采集音视频。
- Release 串行构建：0 警告、0 错误。
- 全量测试：829 项桌面测试 + 183 项 NPEP 测试，共 1012 项通过，0 失败、0 跳过。文档链接与差异检查通过。

本轮证据位于 `.artifacts/recorder-handshake-20261008/`，保留 before、after 和 full 报告。真实录课设备、部署和发行未测试。

## 后续：收尾超时后继续清理

基线 `c345292`。录制器清理等待 5 秒超时后会返回，让进程继续正常收尾，但原代码已提前标记“清理完成”。进程退出后再次清理仍直接跳过，持有的进程句柄没有释放。

现在只有读写任务结束、进程句柄和租约资源释放后才标记完成；超时保留重试机会，并串行处理并发清理。仍使用原来的 5 秒等待，不强杀正在收尾的录制器，不改录制结果。

- 修复前：12 项录制进程测试中，顺序重试／并发清理两个用例失败，10 个已有用例通过；顺序用例直接检查原生进程句柄未关闭。
- 修复后：21 项录制进程、执行记录和 Host 就绪检查通过，0 跳过；确认超时后进程仍在、允许它自行退出后句柄关闭、Saved 状态保留、完成后重复清理正常。
- Release 串行构建：0 警告、0 错误。
- 全量测试：831 项桌面测试 + 183 项 NPEP 测试，共 1014 项通过，0 失败、0 跳过。文档链接与差异检查通过。

本轮证据位于 `.artifacts/recorder-cleanup-20261008/` 的 before、before-concurrent、after 和 full 报告。测试进程用独立临时目录中的释放标记控制退出，并有 30 秒自退出上限；不调用真实录制器或采集设备。真实设备、部署和发行未测试。

## 后续：无效麦克风选择配置

基线 `913e6e9`。`noise-microphone.json` 中的字符串原先未经校验就进入状态回包；超长 ID 使设备查询超过 64 KiB 管道帧限制，客户端收到 EndOfStreamException，监测页无法读取设备列表并重新配置。

现在加载时复用设备选择命令的 ID 校验（非空、不含控制字符、最多 2048 字符），文件超过 16 KiB 时不读取。无效配置显示为尚未选择麦克风，原文件保留；用户明确保存新设备后才替换文件。合法的最长中文 ID 序列化后仍在大小限制内，不会被截断。协议和命令校验规则未改变。

- 修复前：12 项新增测试中，5 个无效 ID 用例和 1 个管道查询用例失败；6 个合法长度及既有坏 JSON 处理对照通过。
- 修复后：94 项噪音采样、管道、学校排程、统计传输及协议回归通过，0 跳过；实际模拟管道验证设备查询、重新保存、状态查询，并验证重新加载和原文件保留。
- Release 串行构建：0 警告、0 错误。
- 全量测试：843 项桌面测试 + 183 项 NPEP 测试，共 1026 项通过，0 失败、0 跳过。文档链接与差异检查通过。

证据位于 `.artifacts/noise-selection-recovery-20261008/` 的 before、after 和 full 报告。仅使用合成设备列表，查询及重新保存未创建采集对象；未进行真实麦克风、WPF 窗口、设备、部署或发行验收。

## 后续：噪音补传文件的会话排除标记

基线 `60b1c85`。补传文件 `noise-outbox.json` 中的 `excludedSessionId` 未经校验就用于旧会话排除。数字、布尔值、数组或对象会在实际监测后的状态查询、停止时保存报告中抛出 InvalidOperationException；无效字符串则被当作正常配置继续使用。

现在加载时要求该可选字段是非空 GUID 的 D 格式字符串；缺字段和 null 仍兼容。无效内容进入既有的 NOISE_STORE_UNAVAILABLE 状态，阻止补传与远程启停，保留原文件；本机采集的状态查询、停止和麦克风释放不受坏标记影响。正常会话排除和旧文件报告处理不变，不修改网络协议。

- 修复前：10 项新增测试中，6 个无效标记用例失败，4 个合法文件对照通过；合成采集直接复现状态查询及清理异常。
- 修复后：155 项噪音监测、管道、排程及补传相关测试通过，0 跳过；验证原文件保留、存储错误提示、本机清理及合法排除行为。
- Release 串行构建：0 警告、0 错误。
- 全量测试：853 项桌面测试 + 183 项 NPEP 测试，共 1036 项通过，0 失败、0 跳过；文档链接与差异检查通过。

证据位于 `.artifacts/noise-outbox-marker-20261008/` 的 before、before-complete、after 和 full 报告。仅操作独立临时目录与合成采集对象；未进行真实麦克风、大屏、部署或发行验收。

## 后续：快捷启动配置恢复

基线 `b3c8ee9`。主窗口和编辑弹窗的快捷启动错误处理漏接 InvalidDataException：未知版本、空记录、缺项目、非法网址、重复／过多项目及超大配置会中断初始化；保存被内容校验拒绝的修改，以及在编辑弹窗保存空名称、不完整网址、非法程序或相对文件路径，也会抛出未处理异常。

生产代码只在两个既有错误处理处补上该异常。无效文件显示“原文件已保留”，快捷项目不部分加载，编辑按钮停用；修复文件后可在同一窗口重新读取。被拒绝的保存显示原有失败提示，列表和文件不变；编辑弹窗显示具体校验提示，不返回已保存结果。不更改配置格式、大小限制或启动行为。

- 修复前：14 组实际 WPF 控件检查中，7 个加载场景、1 个保存场景及 4 个编辑输入场景失败；坏 JSON 与无文件两个对照通过。
- 修复后：14 组全部通过，验证初始化、原文件逐字节保留、按钮状态、重新读取、保存拒绝及编辑输入反馈。使用唯一测试端点的配置文件；不显示窗口、不启动 Host、托盘、轮询或真实快捷项目。
- 解决方案与 WPF 测试项目 Release 构建：0 警告、0 错误。
- 解决方案全量测试：853 项桌面测试 + 183 项 NPEP 测试，共 1036 项通过，0 失败、0 跳过；文档链接与差异检查通过。

专项检查已加入隔离 WPF 默认入口，也可单独运行：

```powershell
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj --no-restore -c Release '-m:1'
$testExe = Join-Path $PWD 'tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe'
$output = Join-Path $PWD '.artifacts/shortcut-recovery-20261008/wpf'
$run = Start-Process -FilePath $testExe -ArgumentList @($output, '--shortcut-recovery') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw '快捷启动恢复检查失败' }
```

证据位于 `.artifacts/shortcut-recovery-20261008/` 的 before-all、after-all 和 full 记录，保留早期 before、before-complete、after 与 after-final 的分阶段证据。仅验证实际初始化／重新读取／保存实现及控件状态，没有显示或截图验收；真实快捷启动、生产大屏、部署和发行未测试。

## 后续：快捷启动配置读写大小一致

基线 `a869d57`。读取快捷启动配置时限制为 128 KiB，保存却没有对应检查。24 个项目均通过单项校验时，较长的中文文件路径经 JSON 转义可使文件超过限制；保存显示成功，下次打开却无法读取整个列表。

现在读写共用 128 KiB 限制，保存前按最终 UTF-8 JSON 字节数检查。超限时不创建临时文件、不替换原配置，也不修改当前列表。界面明确提示减少项目或缩短路径、网址，用户可以在同一窗口继续编辑并保存。普通中文路径、24 项上限、配置格式及原有读取限制保持不变。

- 修复前：首次保存／替换已有配置两个超限用例失败，正常中文路径的 24 项读写对照通过。
- 修复后：17 项快捷启动测试通过，验证单项均合法的超限输入被拒绝、旧文件逐字节保留、首次失败不留下文件，以及合法列表正常读回。
- 隔离 WPF 检查：15 组通过，包含实际保存处理函数的文件与列表保留、主页与管理页的超限提示、编辑按钮仍可使用及修正后正常保存。
- 解决方案与 WPF 测试项目 Release 构建：0 警告、0 错误。
- 全量测试：856 项桌面测试 + 183 项 NPEP 测试，共 1039 项通过，0 失败、0 跳过；199 份 Markdown、932 个本地链接及差异检查通过。

证据位于 `.artifacts/shortcut-size-20261008/` 的 before、after、wpf 与 full 记录。使用隔离临时文件和唯一测试端点，不显示窗口、不启动 Host 或真实快捷项目；真实大屏、部署和发行未测试。
