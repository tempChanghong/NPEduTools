# 噪音监测状态与统计 · 2026-10-07

[迭代索引](README.md) · [噪音监测指南](../GETTING-STARTED.md#噪音监测)

**交付：区分当前采样、已经结束的统计和后台状态未知。** 真实 WPF 窗口的隔离回归确认两个旧问题：断连时旧统计仍被展示；重连后“恢复连接／旧读数”提示仍然残留。修复前记录在本机 `.artifacts/noise-status/before/result.json`，两个断言均为失败。

| 状态 | 页面行为 |
| --- | --- |
| 正在启动、监测或停止 | 显示本次累计统计；实时电平仍按后台返回的采样状态显示 |
| 已停止 | 保留摘要，标为“上次监测统计（已结束）” |
| 采集故障 | 标为“上次监测统计（采集中断）”，保留后台给出的处理建议 |
| 后台连接中断 | 当前状态未知，清空实时电平、趋势和旧统计；不推断监测已经停止 |
| 恢复连接 | 新快照恢复数据与原有按钮状态，清除断连专用提示；操作拒绝的提示仍独立保留 |
| 新后台没有会话 | 显示开始后的统计说明，不沿用旧会话数据 |

统计卡说明：本机摘要不代表报告已经上传；学校端是否收到，应在 NPClassworks 核对。当前本机状态契约没有报告投递状态，本轮不新增“上传成功”推断或进度条。

仅调整 App 展示。不修改 Host、采样算法、学校排程、管理验证、启停命令或网络投递，也不修改网页与 KV。

## 验证

26 项既有采样／电平回归通过。新增隔离 WPF 检查使用真实噪音窗口，覆盖断连与恢复、已结束摘要、采集中断、新后台无会话和最小尺寸滚动布局。测试仅渲染合成快照并使用唯一的不存在管道，不启动实际 App／Host、不采集真实麦克风、不连接学校服务。

App 与界面测试项目 Release 构建通过，0 警告、0 错误。已查看断连、已结束摘要和最小尺寸截图。文档检查覆盖 167 份 Markdown、804 个本地文件链接，`git diff --check` 通过；远程 URL、标题锚点和外部本机资料不在该链接检查范围。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~NoiseTests|FullyQualifiedName~NoiseSignalPresentationTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/noise-status/wpf" --noise-status
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/noise-status/`，不随 Git 分发。本轮不包含真实麦克风、班级大屏、实际断网／重启或生产验收；没有推送、部署或发布。
