<p align="center">
  <img src="images/branding/npedutools-logo.svg" width="96" alt="NPEduTools 图标" />
</p>

# NPEduTools

**教室大屏上的日常工具与学校互联助手。**

NPEduTools 是一款面向 Windows 教室大屏的桌面应用。通过常驻侧边栏打开课堂工具，在主窗口管理录课计划、软件和设置；也可以连接学校的 NPClassworks，接收通知、切换考试环境、开展噪音监测。

[下载](https://github.com/tempChanghong/NPEduTools/releases) · [使用指南](docs/GETTING-STARTED.md) · [反馈问题](https://github.com/tempChanghong/NPEduTools/issues) · [官方网站](https://novark.ink)

本次版本：**v1.0.0 · EVA-01（初号机）**，首个正式版本。[查看更新说明](docs/releases/v1.0.0.md)。

## 能做什么

- **随手打开课堂工具**：常驻侧边栏集中管理应用、文件和网址，并提供 PowerPoint 触摸翻页辅助。
- **快速点名**：连接 SecRandom V3，用侧边栏的“抽”按钮闪抽一人。
- **手动或自动录课**：保存本地微课视频，也可按每周安排、指定日期或 ClassIsland 课表自动录制。
- **切换课堂环境**：联动 ClassIsland 与 ExamAware2，管理日常／考试模式及相关登录自启动，考试期间暂停自动录课。
- **接收学校通知**：通过 NPEP 连接学校的 NPClassworks，接收通知与考试安排，响应学校管理操作。
- **监测教室噪音**：本机分析麦克风信号，支持手动监测和学校定时安排；学校互联只上传统计，不上传录音。

**不连接学校也能使用本机工具。** ClassIsland、ExamAware2 和 SecRandom 按需配置，无需一次装齐。

## 开始使用

适用于 **Windows 10／11 x64**。软件启动时会请求管理员权限。

1. 从[发布页](https://github.com/tempChanghong/NPEduTools/releases)下载 Windows x64 程序包，按该版本说明安装或完整解压。
2. 打开 NPEduTools，跟随初始设置引导选择需要的功能；暂时不用的可以跳过。
3. 用侧边栏快速操作，在主窗口管理录课计划、关联软件和学校连接。

程序包自带 .NET 运行时，无需安装开发 SDK。不同版本的录制组件与功能有所差异，请查看对应发布说明；首次配置见[使用指南](docs/GETTING-STARTED.md)。

## NPEP 学校互联

**NOVARK POWER EDUCATION PLUS（NPEP）** 让学校的 NPClassworks 与大屏上的 NPEduTools 配合工作：网页负责教学信息与管理，桌面端负责本机软件联动、通知展示和麦克风监测。

在“设置 → 学校互联”填写学校提供的服务地址并完成配对，即可使用学校已开通的功能。连接方式及常见问题见[使用指南](docs/GETTING-STARTED.md#连接学校)。

## 帮助与反馈

操作疑问先看[使用指南](docs/GETTING-STARTED.md)；版本变化和已知问题见[发布说明](https://github.com/tempChanghong/NPEduTools/releases)。学校设备上线前，建议先在单台设备核对驱动、插件与定时安排。

欢迎通过 [GitHub Issues](https://github.com/tempChanghong/NPEduTools/issues)反馈问题或建议。请附上版本、操作步骤和错误提示，避免公开账号密码、配对码、设备令牌或师生个人信息。

希望参与开发？构建、调试及架构资料统一从[开发指南](docs/DEVELOPMENT.md)进入。

## 许可证

项目采用 [GNU GPL v3](LICENSE)。第三方组件及其许可证见[依赖说明](docs/DEPENDENCIES.md)，发行包内另附对应材料。

由 **星火动力技术组（NOVARK POWER Technical Group，NPTG）** 开发。
