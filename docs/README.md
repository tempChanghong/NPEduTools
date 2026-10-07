# NPEduTools 文档

[项目首页](../README.md) · [版本说明](releases/README.md) · [历史资料](archive/README.md)

先按用途选择入口。使用指南介绍操作；开发指南介绍当前源码；带日期的任务卡和归档报告记录当时的实现与验证，不能替代当前发布或现场状态。

## 使用与维护

| 想做什么 | 从这里开始 |
| --- | --- |
| 安装并开始使用 | [使用指南](GETTING-STARTED.md) |
| 安装、覆盖升级与卸载 | [安装说明](INSTALLER.md)；[ZIP 使用与回退](PORTABLE-CLASSROOM-GUIDE.md) |
| 完成首次设置 | [首次使用引导](FIRST-RUN-EXPERIENCE.md) |
| 录课、管理计划 | [使用指南：录课](GETTING-STARTED.md#录课)；[微课录制细则](MICROLESSON-RECORDING.md) |
| 点名、快捷启动、触摸翻页 | [SecRandom](SECRANDOM.md) · [自定义快捷](CUSTOM-SHORTCUTS.md) · [PowerPoint 辅助](POWERPOINT-TOUCH-ASSIST.md) |
| 连接学校、接收通知、考试或噪音监测 | [学校互联](npep/README.md) |
| 配置登录启动与关联软件 | [应用登录启动](APP-STARTUP.md) · [ClassIsland 管理员任务](CLASSISLAND-ADMIN-STARTUP.md) · [首次配置检查](CLASSROOM-SETUP-CHECK.md) |
| 查看服务、隐私与开源条款 | [协议与许可证](legal/README.md) |

## 开发与验证

| 想做什么 | 从这里开始 |
| --- | --- |
| 在 Visual Studio 构建与调试 | [开发指南](DEVELOPMENT.md) |
| 理解进程、模块与通信 | [当前架构](ARCHITECTURE.md) |
| 学习 C#、WPF 和源码阅读 | [代码库接管教程](DEVELOPER-LEARNING-GUIDE.md)（按其核对日期阅读） |
| 选择测试入口、判断验证范围 | [测试与验收](TESTING.md) |
| 查协议定义与跨端边界 | [学校互联：协议与源码](npep/README.md#协议与源码) |
| 检查第三方依赖与随包源码 | [依赖](DEPENDENCIES.md) · [第三方材料](THIRD-PARTY-MATERIALS.md) · [FFmpeg 构建](FFMPEG-BUNDLED-BUILD.md) |
| 查实施依据和旧结论 | [迭代记录](iterations/README.md) · [历史资料](archive/README.md) |

## 文档维护规则

- 当前入口只说明行为、命令及阅读顺序，不继续追加每天的进度汇报。
- 功能或命令变更时，更新对应指南；实施过程放入带日期的任务卡。旧研究和被替代的阶段报告归档，保留失败、限制及未验收项。
- 发布说明按版本保存；旧预览、审核和合并记录与已发布版本分开。下载包是否包含功能，以对应版本说明为准。
- 协议正文留在 `legal/`，JSON Schema、示例和验证器留在 `npep/`；这些文件被构建与测试引用，不随普通文档归档。
- 外部项目资料是 Git 子模块，沿用上游目录。它们是参考资料，不是 NPEduTools 的当前使用说明。
- 本地产物路径只是当轮证据线索，不保证随 Git 仓库提供；缺失产物不能补记为通过。远程发布、生产部署和真实设备验收分别记录。

更改文档后运行 `node scripts/check-doc-links.mjs`。它离线检查本仓库 Markdown 的本地文件目标，明确列出排除范围；不验证远程网页或标题锚点。
