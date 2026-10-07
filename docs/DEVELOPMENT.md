# NPEduTools 开发指南

[文档首页](README.md) · [当前架构](ARCHITECTURE.md) · [测试与验收](TESTING.md)

## 在 Visual Studio 中运行

1. 安装支持项目 SDK 的 Visual Studio 和“.NET 桌面开发”工作负载。
2. 安装根目录 [global.json](../global.json) 指定的 SDK。当前基线为 **10.0.400**，允许同系列最新补丁；不要沿用 ClassIsland 本体的 SDK 选择。
3. 打开 [NPEduTools.sln](../NPEduTools.sln)，等待 NuGet 还原；将 **NPEduTools.App** 设为启动项目。
4. 选择 **Debug**，按 **F5** 构建并运行。需要调试管理员进程时以管理员身份运行 IDE；主应用正常启动也会请求提权。
5. 在测试配置中保存外部软件路径，按需要安装插件、连接本地学校服务。软件切换、录屏和麦克风操作会产生实际效果。

App 构建会复制所需的 Host、Guard、Admin 和 Recorder 目录，不要单独移动 App EXE。开发机录制组件准备与随包源码见 [FFmpeg 构建](FFMPEG-BUNDLED-BUILD.md)；发行包的内置组件要求见[第三方材料](THIRD-PARTY-MATERIALS.md)。

调试受保护定时监测前，先使用托盘的正常停止后台与退出入口，并完成管理验证。只关闭窗口会继续工作，直接终止 App／Host 可能触发 Guard 恢复。[守护实施记录](iterations/SCHEDULED-NOISE-GUARD-20261004.md)说明保护范围。

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
