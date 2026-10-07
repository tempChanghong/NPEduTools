# NPEP 学校互联

[文档首页](../README.md) · [使用指南](../GETTING-STARTED.md#连接学校) · [测试与验收](../TESTING.md)

NPClassworks 负责作业板、学校管理和操作入口；NPClassworksKV 负责身份、权限、设备请求与报告存储；NPEduTools 负责大屏本机的软件、通知、录课和麦克风能力。大屏网页经服务端与桌面协作，不直接控制本机管道，也不重复采集已交由桌面负责的麦克风。

## 功能入口

| 功能 | 当前阅读入口 | 实施依据 |
| --- | --- | --- |
| 连接与配对 | [填写服务地址、完成学校连接](../GETTING-STARTED.md#连接学校)；分离部署填写 API 根地址 | [预授权配对](../iterations/NPEP-PREAUTHORIZED-PAIRING-20261002.md) |
| 学校通知 | [使用指南](../GETTING-STARTED.md#连接学校)；配对后由桌面显示通知，网页控制投递 | [投递](../archive/npep/NPEP-N2-DELIVERY-REPORT.md)、[自定义窗口](../archive/npep/NPEP-N2-CUSTOM-NOTIFICATION-UI.md) |
| 考试与返回日常 | [考试用例与操作入口](EXAM-MODE-OVERVIEW.md) | [考试优先级](../archive/npep/EXAM-PRIORITY-20260928.md)、[方案投递](../archive/npep/EXAMAWARE-PLAN-REMOTE.md) |
| 原生噪音监测 | [使用指南：噪音监测](../GETTING-STARTED.md#噪音监测)；本机分析，只上传统计 | [网页与桌面边界](../archive/npep/N4.2-NOISE-TAKEOVER.md) |
| 学校定时监测与保护 | 网页安排学校时段，桌面执行；返回作业板不停止采集 | [显示与保护](../iterations/SCHEDULED-NOISE-INTEGRATED-20261004.md)、[Guard](../iterations/SCHEDULED-NOISE-GUARD-20261004.md) |

配对授权、麦克风和远程控制的告知与职责见[服务及隐私协议](../legal/README.md)。功能是否包含在下载包中，以[对应版本](../releases/README.md)为准。

OOBE 和设置页共用学校连接控件：已配对不代表当前在线；等待自动重试、主动暂停、连接停止、学校停用及后台未确认分别提示。按当前建议等待、恢复或联系管理员；原始状态与错误码可展开技术详情复制。实现与本机验证范围见[连接提示任务卡](../iterations/SCHOOL-CONNECTION-PRESENTATION-20261007.md)。

## 协议与源码

协议文件保持稳定路径，供应用嵌入和三端契约测试使用。编号区分通道与历史阶段，不应仅凭 N1／N3／N4 文件名推断当前产品只支持哪些功能。

| 通道 | 本仓库定义 |
| --- | --- |
| 配对、设备身份与状态 | [N1 基础契约](NPEP-N1-CONTRACT.md)、[Schema](n1-wire.schema.json)、[示例](n1-examples.json) |
| 远程考试／日常模式 | [Schema](n3-wire.schema.json)、[示例](n3-examples.json) |
| 考试方案 | [Schema](n4-exam-plan.schema.json) |
| 噪音状态与报告 | [Schema](noise.schema.json) |
| 排程规则与下发 | [规则](noise-schedule-policy.schema.json)、[下发](noise-schedule-wire.schema.json) |
| 定时监测管理 | [共享示例](noise-management-wire-cases.json)；服务端校验见 [KV 实现](https://github.com/tempChanghong/NPClassworksKV/blob/main/domain/npep/noiseManagement.js) |
| 定时页面在线状态 | [共享示例](noise-display-presence-wire-cases.json)；服务端校验见 [KV 实现](https://github.com/tempChanghong/NPClassworksKV/blob/main/domain/npep/noiseDisplayPresence.js) |

网络会话从 [NPEP 集成模块](../../src/NPEduTools.Integrations.Npep)读起；本机服务组合见 [Host](../../src/NPEduTools.Host/Program.cs)，进程分工见[当前架构](../ARCHITECTURE.md)。三端检查使用 [CURRENT 入口](../TESTING.md#三端当前组合)，专项任务卡在[迭代索引](../iterations/README.md)。

## 历史记录

本页只维护阅读入口。旧进度汇报完整保存在[2026-09-20 至 2026-10-04 进度快照](../archive/npep/PROGRESS-20260920-20261004.md)；各阶段设计、现场、备份和部署报告在 [NPEP 归档](../archive/npep/README.md)。旧文中的“未实现／未推送／未部署”描述只属于原日期，不是当前待办。
