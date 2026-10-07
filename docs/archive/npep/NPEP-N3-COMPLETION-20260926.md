# N3 本地闭环与验收记录

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

日期：2026-09-26。本文件汇总当前实现与验收，早期阶段文档中的“尚未接通”等表述仅反映当时状态。未提交、推送、合并或部署生产，未生成应用发布包；仅编译测试程序集，应用仍由用户在 IDE Debug 运行。

## 本轮交付

N3 的固定操作为单设备 `EXAM / CURRENT_RUNTIME`。学校 OWNER／ADMIN 在网页核对设备后发起；设备必须已配对并在本机开启一次许可。桌面领取后重新检查权限、版本、进程身份、录制空闲和交互条件，再持久化暂停、准备 ExamAware2、核实桥接、正常退出 ClassIsland、回传结果。

不会修改 Windows 自启动，不停止已有录制，不强制结束进程，也不提供远程返回日常模式。现场结束只解除本次远程考试暂停，保留录课计划、其他暂停和历史结果。

本轮补齐了：

- 原生 PostgreSQL 隔离验收入口：自动创建临时集群、随机回环端口、独立测试库和密码，不连接或修改 `classworks_debug`，不要求开发用户具有 CREATEDB 权限，不调用 Docker。
- 真实 .NET 客户端 → Node HTTP → PostgreSQL 联调，验证首次许可、成功／部分完成、同请求幂等、现场结束及重启不重放。仅 OS 动作在此测试中模拟，网络、客户端协议与数据库均实际执行。
- 网页显示最近运行环境、最后执行步骤、逐项设备回执及可读失败原因；明确区分历史回执和当前状态，终态不因回执变旧而误显示“没有近期进度”。
- 本机已配对设备的实际网页操作与现场结束核对，见下文。

## 一条命令验证

在 NPEduTools 根目录、Windows PowerShell 5.1 或 PowerShell 7 中执行：

```powershell
./scripts/test-npep-n3.ps1 -Database
```

需要三个仓库已还原依赖、Node、项目要求的 .NET SDK 和已安装的原生 PostgreSQL 工具。Windows 自动寻找 `C:\Program Files\PostgreSQL\<版本>\bin`，其他位置可先设置：

```powershell
$env:NPEP_TEST_PG_BIN = '实际 PostgreSQL 的 bin 目录'
```

非同级仓库可用 `-WebRoot`／`-BackendRoot`；桌面目录自动传给数据库验收。后端单独入口为 `pnpm test:npep:native`，默认寻找同级 NPEduTools；自定义桌面目录用 `node scripts/run-native-npep-tests.js --desktop-root <路径>`。

测试输出位于 `.artifacts/n3-tests`，不覆盖 IDE 的 Debug 输出。临时数据库成功后停止并清理；失败保留诊断文件并停止实例。首次实现遇到 Windows 子进程继承输出管道导致启动等待，已改为文件日志，后续运行正常。其他旧 `test:database` 等入口仍可能使用 Docker，不在本命令路径中。

自动化结果（220 项及额外契约校验）：

| 范围 | 通过数 |
| --- | ---: |
| 网页 API、状态、呈现 | 8 |
| 后端状态机、HTTP 错误关联、本地初始化边界 | 17 |
| 桌面执行内核、本机适配器 | 54 |
| 桌面 N1／N2／N3 传输回归 | 121 |
| 原生 PostgreSQL、HTTP、真实 .NET 跨端验收 | 20 |

共享契约另验证 29 个正反例、三端 Schema 一致性、真实网页请求字段，以及禁止路径、自启动、批量操作和 DAILY 扩展。网页修改通过 ESLint。

## 当前开发电脑的真实验收

通过实际浏览器登录 `http://localhost:3031/classworks-admin`，进入“大屏设备 → NPEP 设备互联 → 考试环境”。使用本地测试学校已配对设备，没有使用生产账户或服务。

第一轮初始条件：ExamAware2 已就绪，ClassIsland 已退出，录制空闲，本机许可已开启，远程暂停未建立。因此这一轮证明“已满足软件条件”的闭环，不证明从未运行状态启动软件。

- 网页确认后创建 `58e35cea-7308-4927-a88b-7ab3eaf02a3e`。
- 真实 Host 回传 `SUCCEEDED`，网页显示“考试环境已就绪”；回执为 ExamAware READY、ClassIsland EXITED、远程暂停已建立、自启动 NOT_REQUESTED。
- 浏览器无页面异常；截图和记录在 NPClassworks `test-results/n3-live-local-result.png`／`.json`。
- 用户在桌面执行核实、结束。服务端记录 `localEndedAt=2026-09-26T12:05:30.826Z`，保留原 `SUCCEEDED` 历史；后续状态 `remoteExamPause=false`。
- 当前工具账号可读取的相关计划任务和 HKCU 登录自启动快照前后相同。工具进程无法访问管理员 Host 管理管道，现场结束由用户完成，未用服务端写库替代本地确认。

第二轮：尚未完成。启动 ClassIsland 时出现“等待课程接口”，检查 Windows Application 事件（2026-09-26 20:09:57、20:10:21、20:10:58）确认本体在初始化阶段崩溃：HotAvalonia 仍引用搬迁前 `D:\WebstormProjects\ClassIsland\ClassIsland\App.axaml.cs`，当前程序位于 `D:\CodeProjects\ClassIsland`。异常为 `FileNotFoundException`，发生于 `App.Initialize()` 的 `EnableHotReload()`，课程接口尚未建立。需在新目录重新生成 ClassIsland Debug 程序后重试；本轮未重新生成该程序。

同时修正 NPEduTools 启动验证的诊断缺陷：已有进程在接口读取期间退出，也应返回 `ClassIslandExited`，而不是继续等待到超时。新增四个回归场景，覆盖启动／仅验证入口，以及接口读取失败／返回旧成功数据时进程已退出的情况；均不得重新启动程序。此修复不代表第二轮实机退出验收已通过。

修复后单独运行 `LaunchTests`：19 项通过、0 失败。仅将测试程序集编译到 `.artifacts/n3-tests`，没有覆盖正在运行的应用；上方 220 项为此前统一验收结果。

## 仍需区分的验收与发布边界

- 自动化中的模拟动作不代表实际录制、UAC、屏幕锁定、Windows 重启和真实软件拒绝退出都已现场验证。
- 当前设备是开发电脑；目标班级大屏需要配套桥接、本机许可、录制空闲及程序路径预检后再试点。
- 本地历史／投递账本仍有 256 项上限，达到上限会拒绝新执行；归档功能未纳入这次收尾。网页显示最近 20 项。
- 不改变用户已暂缓的 ExamAware2 编辑器退出问题：N3 不远程关闭 ExamAware2。

发布顺序：先审核三仓差异与此验收记录；按现有部署流程备份并升级后端（包含 `20260926000000_npep_runtime_control` 迁移），再发布网页和配套桌面版本；保持设备控制默认未许可，在一台目标大屏手动开启后验收。不能把本地通过等同于生产已经部署。
