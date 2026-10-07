# 考试结果展示 · 2026-10-07

[迭代索引](README.md) · [考试操作指南](../npep/EXAM-MODE-OVERVIEW.md)

**交付：让桌面检查结果与考试历史可读、可排查。** 原界面把 `PARTIAL / SetStartup / STARTUP_NOT_READY` 与请求编号直接拼成记录；现在用中文描述结果、最后步骤、原因和下一步建议，原始值放在默认折叠、可复制的技术详情中。

当前录课暂停与历史回执分别展示。解除本机暂停不改写原切换结果；历史成功不代表软件此刻仍就绪；缺少状态、存储失败或未知结果不显示为成功。当前暂停的请求编号仍可从详情读取，不依赖最近八条历史。

本轮仅修改 App 展示，不改 Host 执行、协议、权限、按钮启用条件或重试逻辑，不新增远程操作。历史没有逐步骤完成清单，所以界面只说“最后记录的步骤”，不推测此前步骤已完成。

## 验证

- 新增展示语义测试，并运行既有考试执行回归。
- 隔离 WPF 控件测试使用真实资源、模板和绑定，检查中文记录、详情折叠／复制、本机解除记录及空／不可用刷新；不启动 MainWindow、Host 或教学软件。
- Release 构建、文档链接及差异检查。

本机结果：72 项考试相关测试通过，隔离 WPF 组件检查通过；锁定依赖还原及 App／界面测试项目 Release 构建通过（0 警告、0 错误）。文档检查覆盖 165 份 Markdown、796 个本地文件链接；`git diff --check` 通过。远程 URL、标题锚点和外部本机资料没有据此验收。

复现命令（在仓库根目录，Windows PowerShell 5.1 可用）：

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~RemoteExamPresentationTests|FullyQualifiedName~RemoteExamTests|FullyQualifiedName~RemoteDaily'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/exam-presentation/wpf" --remote-exam
node scripts/check-doc-links.mjs
```

界面测试复用现有隔离 WPF 测试项目；`--remote-exam` 仅运行考试记录组件，省略该参数仍运行原 OOBE 测试。结果与预览图写入指定目录，单元测试证据另存 `.artifacts/exam-presentation/unit/`。这些本机产物不随 Git 分发。

真实大屏操作、UAC、教学软件切换与生产验收本轮未运行；本机自动检查不代表这些环节通过。本轮本机结果不包含推送、部署或发布。
