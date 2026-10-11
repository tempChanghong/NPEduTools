# NPEP 运维改动审核索引 · 2026-10-04

| 任务卡 | 内容 |
| --- | --- |
| 本轮交付 | 最新运维改动的独立干净源码复验及简短审核索引 |
| 包含 | GitHub CI 来源锁定、桌面诊断摘要对照、完整 .NET 传输联调 |
| 排除 | 安装器／FFmpeg 打包、录课页面／README 其他改动、NPEssentials、生产部署 |
| 完成条件 | 选定文件逐一哈希一致、固定三仓提交、完整 `-RequireClean` 验收、未覆盖项目明确列出 |

## 这几轮实际改了什么

| 项目 | 行为 | 主要代码 |
| --- | --- | --- |
| CI | PR／手动工作流使用三个完整 SHA，拒绝脏工作区和运行中源码变化 | `.github/workflows/npep-operations.yml`、`scripts/resolve-npep-operations-ci.mjs`、`scripts/npep-operations-ci.json` |
| 诊断对照 | 桌面导入学校 JSON，核对服务和大屏绑定，解释旧记录与差异；不上传、不执行控制 | `src/NPEduTools.App/NpepDiagnosticComparison.cs`、`NpepConnectionControl.xaml(.cs)`、`NpepConnectionSession.cs` |
| 统一验收 | 跨端真实导出代码生成四组摘要，再由 C# 对照；数据库阶段不再跳过五类传输场景 | `scripts/test-npep-operations.mjs(.ps1)`、`scripts/check-npep-operations-contract.mjs` |
| 回归 | 覆盖格式／大小、归属、过时、取消、源码锁定和缺失／跳过／重复证据 | `tests/NPEduTools.Tests/NpepDiagnosticComparisonTests.cs`、`tests/operationsAcceptance.test.mjs`、`tests/operationsCi.test.mjs` |

只需先看这张表。具体细节分别见 [CI](NPEP-OPERATIONS-CI-20261004.md)、[摘要对照](NPEP-DIAGNOSTIC-COMPARISON-20261004.md)、[完整传输联调](NPEP-OPERATIONS-TRANSPORT-20261004.md)。此前人工验收没有因本轮自动化通过而改为完成。

## 复验来源

从三个本地 Git 仓库新建隔离检出，不复制 `.env`、配对凭据或用户配置。桌面以 `7fc2a4d6debcf12e657c60c89a79d7e2cdf4b060` 为基线，仅复制 20 个明确选定的运维文件，逐一核对 SHA-256；创建临时提交后执行严格验收。

| 来源 | 完整 SHA |
| --- | --- |
| 桌面临时测试提交 | `93282f69f3d612bcb6fab60e1333c23afdc31a44` |
| 网页固定提交 | `64d97756f9a3ea6694a335b5b1302f7d67750f12` |
| KV 固定提交 | `bd6203c6cb9141f117a457eae385723274d4f07e` |

桌面测试提交只存在于 `.artifacts/operations-ci-local/20261004-current-6686e37a/NPEduTools`，不是当前开发分支提交，不是 Release，也没有推送。前后端提交与 CI 登记相同，不跟随分支最新值。

同目录上层的 `snapshot.json` 保存源基线、20 个路径和复制字节哈希。`operations-review.patch` 是该临时提交相对于桌面基线的完整补丁；`operations-review-readable.patch` 忽略行尾空白差异，仅供阅读。测试项目文件含已有 CRLF／LF 混合换行，阅读补丁避免把换行差异看作整文件业务改动；完整补丁及测试保留实际字节。没有修改维护者的 Git 换行配置。

在隔离桌面目录执行：

```powershell
./scripts/test-npep-operations.ps1 -Browser -Database -RequireClean `
  -DesktopCommit 93282f69f3d612bcb6fab60e1333c23afdc31a44 `
  -WebCommit 64d97756f9a3ea6694a335b5b1302f7d67750f12 `
  -BackendCommit bd6203c6cb9141f117a457eae385723274d4f07e
```

这验证的是 GitHub 运维工作流调用的实际验收入口和完整来源组合；没有在 GitHub 执行 runner 设置、下载或工件上传，因此不能称为云端 CI 通过。

## 本轮结果

PowerShell **5.1.26100.9444** 下完整严格验收 **PASSED**，`immutable=true`、`sourceStable=true`；三个隔离工作区测试前后均干净。结束后再次核对，开发工作区中的 20 个选定文件仍与快照逐字节一致。

| 检查 | 结果 |
| --- | --- |
| 来源／入口回归 | 10 通过 |
| 桌面测试 | 59 通过 |
| 两端导出与摘要对照 | 四组实际生成的合成状态全部通过 |
| WPF 编译 | 通过，未启动应用 |
| 后端／网页测试 | 20 + 5 + 7 通过 |
| 浏览器 | 3 通过，无跳过或重试不稳定项 |
| 迁移／数据库业务联调 | 6 + 45 通过，零跳过，包含五类 .NET 传输场景 |
| 临时数据库 | 已停止并清理 |

验收时间为 2026-10-04 11:59:14～12:03:27 UTC。摘要证据位于 `.artifacts/operations-ci-local/20261004-current-6686e37a/review-result.json`；完整报告位于该目录的 `NPEduTools/.artifacts/npep-operations/2026-10-04T11-59-14-818Z-0d3cef22/result.json`，同目录有分项日志和 TRX。

本轮复验没有发现需要修改业务代码的问题；仅新增本审核索引及开发指南入口。真实设备、人工界面、生产和云端运行仍为 `NOT_RUN`。

## 接手顺序

1. 先审核上述三个功能的补丁和测试，确认导入只读、不自动重配或强制切换；核对两份依赖 SHA。
2. 获准提交／推送后，在开发仓库分组提交，更新来源登记并检查真实 GitHub CI。临时测试提交不能直接当成正式发布提交。
3. 暮至长虹回到设备旁后，补测 JSON 文件选择与显示、切换归属清空，以及班级大屏录课／监测／考试现场行为。
4. 发布前从最终审核提交重建安装包和 ZIP，再按交付任务卡验收。早先候选不包含全部最新运维改动。

本轮未提交当前分支、推送、合并、打包或部署。没有改 NPEssentials；其他未提交改动保留。
