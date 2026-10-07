# 自动录课状态与操作反馈 · 2026-10-07

[迭代索引](README.md) · [自动录课指南](../GETTING-STARTED.md#自动录课)

**交付：失败的操作提示不再遮住实际录制状态。** 原窗口把 `_automaticError` 放在新状态前面，一次失败后，后续后台录制、失联或恢复的消息都被旧错误盖住。隔离 WPF 回归通过真实“开启自动录课”按钮触发缺少设置的分支，再投递合成录制回执；修复前的失败证据在本机 `.artifacts/automatic-status/before/result.json`。

| 信息 | 展示位置与含义 |
| --- | --- |
| 实际录制状态 | 持续显示后台最新返回的状态及状态错误，不受上次操作失败影响 |
| 请求处理中 | 独立显示“本次操作：正在等待后台确认”，不提前认定操作成功 |
| 操作失败／设置不可读 | 显示“上次操作提示”，保留原因；不替换实际状态 |
| 操作得到成功回执 | 清除操作提示，实际状态仍以后台回执为准 |
| 实际执行记录／试运行 | 保留原入口和独立性，不把模拟结果当作已生成视频 |

仅修改自动录课窗口的展示与反馈。不改录制客户端、Host、计划、学校时间、截止／租约、权限、配置格式或采集组件。网页与 KV 未改。

## 验证

58 项既有计划／执行回归通过。隔离 WPF 检查使用真实窗口、真实按钮验证与录制客户端的状态分发，覆盖缺少设置、不可读设置文件保留、新状态不受旧提示遮挡、失联与恢复、执行记录、考试暂停提示、设置入口、试运行独立性及 1040×700 布局。App 与界面测试项目 Release 构建通过，0 警告、0 错误。

已查看恢复后的状态与最小尺寸截图。文档检查通过：168 份 Markdown、808 个本地文件链接；远程 URL、标题锚点和外部本机资料不在检查范围。`git diff --check` 通过。

测试取消客户端轮询，仅应用合成回执；学校时间请求使用唯一的不存在管道。没有启动 Host、录制器、设备检测或真实采集，没有执行实际开启／关闭录课命令。唯一管道对应的测试配置在结束时清理，不使用用户配置。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~RecordingExecutionTests|FullyQualifiedName~RecordingPlannerTests|FullyQualifiedName~CalendarRecordingPlannerTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/automatic-status/wpf" --automatic-recording
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/automatic-status/`，不随 Git 分发。实际请求的等待／成功／拒绝展示已修改，但本轮没有实测后台命令或真正生成视频；真实录课、班级大屏及生产验收未运行。本轮不包含推送、部署或发布。
