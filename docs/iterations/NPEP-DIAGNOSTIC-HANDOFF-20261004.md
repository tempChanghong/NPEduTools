# NPEP 故障诊断摘要 · 2026-10-04

| 任务卡 | 内容 |
| --- | --- |
| 主交付 | 桌面互联与学校设备概览的本地 JSON 诊断摘要 |
| 用途 | 对照服务地址、归属编号、模式和最近观测，减少依赖截图猜测故障 |
| 边界 | 复用已有只读接口；不采集新信息、不导出原始日志、不自动上传、不修改后端与 NPEssentials |
| 验收 | 字段白名单、缺失／过时数据、读取并发与关闭、权限变化、浏览器实际下载 |

## 使用

1. 大屏：**设置 → 学校互联 → 故障排查 → 导出诊断摘要**，选择保存位置。OOBE 复用同一连接控件，也有此入口。
2. 网页：**学校管理 → 大屏设备 → 打开 NPEP 设备互联 → 学校运维概览 → 导出诊断摘要**。
3. 核对文件后再自行分享。两边以学校、行政班和大屏绑定 UUID 对照；服务地址也应一致。编号用于定位记录，不是配对凭据。网页摘要不含设备名称或搜索词。

桌面依次读 `npep.status` 与 `host.cached-status`，每次读取限时三秒，两段有各自的读取时间；不会启动课程探测或改变互联状态。后台读取失败仍生成摘要，`available=false`、缺失字段为空或 `UNKNOWN`；不会把旧的健康状态填回新摘要。导出期间共用连接操作锁；窗口生命周期结束后取消导出。

`localCache` 是已有本机缓存，不是重新检查；模式核查时间单独为 `modeCheckedAt`。连接的 `lastReceivedAt` 是学校最近接收时间。导出时间与读取时间为本机 UTC 元数据，**不替代 ClassIsland 学校时间或改变排程依据**。

网页保留全校计数，但只导出当前已加载设备、最多 200 台：`matchedTotal`、`loadedCount`、`exportedCount`、`omittedLoadedCount`、`hasMore` 明确区分。页面间可能采于不同时间；摘要不声称全校设备细节齐全或跨页原子快照。`asOf` 是服务器查询时间，`generatedAt` 沿用页面估算的服务器时间；刷新失败或观测超过一分钟设置 `stale=true`。权限失效、切换学校或面板关闭按既有流程清空数据，没有摘要时不允许导出。旧后端不支持概览时不伪造汇总摘要。

## 导出范围

明确投影版本、状态枚举、布尔值、计数、时间与业务 UUID；未知枚举写 `UNKNOWN`。包括服务 origin（保留自定义端口）、归属编号，桌面还包含服务实例／部署代际 UUID 与当前操作编号。排除配对码、请求体、密码／令牌／密钥、姓名和设备名称、执行文件与用户目录路径、课程／通知正文、原始日志与音频。URL 的路径、查询、片段不导出，含用户名密码的 URL 不导出。文件只在用户选择的本机位置保存或浏览器下载。

## 代码入口与验证

桌面字段白名单：`src/NPEduTools.App/NpepDiagnostics.cs`；读取：`NpepConnectionSession.CollectDiagnosticsAsync`；保存入口：`NpepConnectionControl.xaml(.cs)`。

网页字段白名单：`NPClassworks/src/utils/npepDiagnostics.js`；入口：`NpepDeviceOverview.vue`、`NpepDeviceManager.vue`。后端不变，不迁移数据库。

```powershell
dotnet test tests/NPEduTools.Tests/NPEduTools.Tests.csproj --filter "FullyQualifiedName~NpepDiagnosticsTests|FullyQualifiedName~NpepConnectionSessionTests|FullyQualifiedName~CachedHostStatusTests"
# 在 NPClassworks：
node --test tests/npepDiagnostics.test.js tests/npepDeviceOverview.test.js
pnpm exec playwright test tests/e2e/admin-screens.spec.js --grep "NPEP school overview"
```

## 本轮结果

| 验证 | 结果 |
| --- | --- |
| 桌面针对性回归 | 33 通过、0 失败、0 跳过；包括摘要白名单、读取失败后不复用旧健康状态、并发／取消、共享配对会话与缓存读取无探测 |
| WPF 编译 | 最终 Debug 构建成功，0 警告、0 错误；未运行应用、未生成发布 ZIP |
| 网页全量单元测试 | 686 通过、0 失败、1 项可选桌面合成夹具测试跳过 |
| 浏览器流程 | 1 项真实浏览器流程通过：正常下载并解析 JSON、刷新失败导出过时快照、403 后清空入口，390px 深浅主题无横向溢出 |
| 静态检查 | 网页全量 ESLint 与两仓库 `git diff --check` 通过 |
| 人工／大屏／生产 | `NOT_RUN`，不计为通过；未重测真实麦克风、考试切换或数据库迁移 |

证据目录：`D:\CodeProjects\NPEduTools\.artifacts\npep-diagnostics-20261004`，包括 TRX、桌面测试日志、最终 WPF 构建日志、网页全量单元／ESLint 日志、窄屏截图及浏览器运行状态记录。浏览器数据来自隔离合成学校，不是生产或真实学校资料。

一次只指定 MSBuild `Compile` 目标的验证未运行完整 WPF XAML 生成步骤，出现缺少生成成员的错误；改用正常 `dotnet build --no-restore` 后通过，没有据此修改业务代码。

未提交、推送或部署；真实大屏、生产及人工文件保存验收不计为通过。之前的安装包候选没有重打包，本次修改需在 IDE 重新编译后查看。人工接手时只需分别导出一次摘要，检查大屏绑定编号、服务 origin 与观测时间，确认文件没有配对码或路径，再与网页摘要对照。
