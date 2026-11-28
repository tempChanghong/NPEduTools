# NPEduTools 开发指南

[文档首页](README.md) · [当前架构](ARCHITECTURE.md) · [测试与验收](TESTING.md)

## 在 Visual Studio 2026 中调试

1. 安装 Visual Studio 2026 的「.NET 桌面开发」工作负载和 [global.json](../global.json) 指定的 SDK。当前基线为 **10.0.400**，允许同系列最新补丁。
2. 以管理员身份开启 Visual Studio，打开 [NPEduTools.sln](../NPEduTools.sln)，选择 **Debug**。在启动下拉列表选 **NPEduTools · App + Host（隔离调试）**，按 **F5**。可以分别在 App 的 `OnStartup` 和 Host 的 `PipeServer` 请求入口下断点。按 **Shift+F5** 停止会话。
3. 首次启动时在调试实例完成协议确认。App、Host 和 ClassIsland 测试端点均使用专用管道，Host 数据保存在工作区 `.artifacts/visual-studio-debug/host`。Visual Studio 管理两个进程，App 跳过自动启动 Host 和登记守护任务的步骤。

隔离调试适合页面和 IPC 开发。需要长时间停在 Host 建立管道监听之前时，可以先单独调试 **隔离调试 Host**，继续执行到它开始监听，再在项目菜单中调试 **隔离调试 App**。需要调试 Recorder 时，在「调试 → 附加到进程」按 PID 选择本次启动的 Recorder。

要检查正常启动、守护或安装场景，在启动项目 `NPEduTools.App` 下选 **NPEduTools.App** 启动配置。在测试配置中保存外部软件路径，按需安装插件和连接本地学校服务。软件切换、录屏和麦克风操作会产生实际效果。

App 构建会复制所需的 Host、Guard、Admin 和 Recorder 目录，不要单独移动 App EXE。开发机录制组件准备与随包源码见 [FFmpeg 构建](FFMPEG-BUNDLED-BUILD.md)；发行包的内置组件要求见[第三方材料](THIRD-PARTY-MATERIALS.md)。

调试受保护定时监测前，先使用托盘的正常停止后台与退出入口，并完成管理验证。守护现在会继续待命，换目录或维护前还需按 [守护维护说明](GUARD-MAINTENANCE.md)撤销本实例的独立启动登记。只关闭窗口会继续工作，学校自习准备／监测时段直接终止 App／Host 可能触发恢复。当前范围见 [生命周期实施卡](iterations/STUDY-GUARD-LIFECYCLE-20261011.md)。

## 本地检查

在仓库根目录执行：

```powershell
./scripts/verify.ps1
node scripts/check-doc-links.mjs
```

前者执行锁定依赖还原、Release 构建和解决方案测试；后者检查文档本地文件链接。已有 Release 构建时，可用 `./scripts/start-app.ps1` 启动，它本身不构建。

三端整体检查优先使用 [CURRENT 入口](TESTING.md#三端当前组合)。配对、考试、定时监测和交付仍保留专项入口；按改动范围选择，不把缺少浏览器、数据库或真实设备检查的结果写成完整验收。

跨仓库联调使用隔离本地服务与原生 PostgreSQL。测试报告应记录源码提交及工作区是否有改动；代码检查、合并、打包、部署和现场验收分别说明。

## 源码与参考资料

- [当前架构](ARCHITECTURE.md)：先理解进程与模块，再阅读具体服务。
- [代码库接管教程](DEVELOPER-LEARNING-GUIDE.md)：Visual Studio、C#、WPF 与源码导航；旧版本和文件位置按当前仓库核对。
- [学校互联](npep/README.md)：产品流程、跨端协议和测试入口。
- [安装器](INSTALLER.md)：Inno Setup、统一打包与隔离生命周期检查。
- [迭代索引](iterations/README.md)：按主题查设计和实施记录。
- [早期本地环境](archive/development/LOCAL-DEVELOPMENT.md)：原开发机的目录和 SDK 快照，不是新机器安装清单。
- [第三方子模块](archive/README.md#外部参考资料)：ClassIsland、ExamAware2 和 PowerPoint 资料使用固定上游提交；按需要初始化对应子模块。
