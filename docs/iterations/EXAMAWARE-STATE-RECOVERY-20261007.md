# 考试看板状态与回执恢复 · 2026-10-07

[迭代索引](README.md) · [软件配置指南](../GETTING-STARTED.md#关联教学软件)

**交付：考试看板页不再用旧连接状态或旧方案覆盖新的操作结果。** 真实 WPF 窗口和唯一模拟 Host 管道已复现：操作丢失回执后仍显示桥接已连接；较早查询随后返回，把新校验的方案摘要换回旧摘要。请求等待期间，放映按钮也未随其他操作一起禁用。

| 情况 | 页面行为 |
| --- | --- |
| 正在处理请求 | 统一暂停配置、配对、退出、自启动及放映操作，显示等待确认 |
| 后台未确认／请求丢失回执 | 连接、版本、自启动与放映显示未知，清除可启动的方案引用；不认定软件已退出，不自动重发操作 |
| 新操作已开始 | 忽略较早查询返回的快照或错误，避免退回旧摘要和状态 |
| 后台恢复、桥接未连接 | 可保存程序位置、打开软件及处理配对；桥接相关操作仍按能力与状态可用 |
| 新快照确认桥接就绪 | 恢复相应控制；有活动放映时不能重复开始，路径编辑和无效新文件的旧方案拦截规则保留 |
| 配对导出失败 | 后台失联显示未知；仅本地文件写入失败则保留刚确认的后台状态 |

仅修改 App 的考试看板窗口及测试。Host、桥接插件、远程切换、方案内容校验、配置修订、权限和 Windows 自启动执行逻辑不变。导出文件对话框保持原入口，文件写入部分单独提取以验证不同失败原因。

## 验证

- 两处修复前失败证据：`.artifacts/examaware-status/before/result.json`、`ordering-before/result.json`；分别记录丢失回执后保留旧连接，以及旧查询恢复旧方案。
- 5 组隔离界面检查通过：方案请求时序、当前查询失联、操作丢失回执、恢复及无效文件拦截、配对导出的连接／文件错误。使用真实 App 请求和协议，保留修订核对；模拟方案摘要不代表真实考试文件已校验。
- 已查看 650×540 窗口中连接与方案未知的滚动截图。
- 61/61 项既有 ExamAware 回归通过，无跳过，覆盖本机协议、放映、退出、自启动及远程考试／日常切换。App 与界面测试项目 Release 构建通过，0 警告、0 错误。

```powershell
./scripts/dotnet.ps1 test tests/NPEduTools.Tests/NPEduTools.Tests.csproj -c Release --filter 'FullyQualifiedName~ExamAwareTests'
./scripts/dotnet.ps1 build tests/NPEduTools.Onboarding.UiTests/NPEduTools.Onboarding.UiTests.csproj -c Release
& ./tests/NPEduTools.Onboarding.UiTests/bin/Release/net10.0-windows/NPEduTools.Onboarding.UiTests.exe "$PWD/.artifacts/examaware-status/wpf" --examaware-status
node scripts/check-doc-links.mjs
```

证据位于 `.artifacts/examaware-status/`，不随 Git 分发。新增界面测试只使用唯一模拟管道、合成状态和虚构配对数据；临时文件在结束时清理，不调用真实文件对话框，不启动 ExamAware，不放映、不改 Windows 自启动或真实配对。真实软件、班级大屏及生产验收未运行，本轮不含推送、部署或发布。
