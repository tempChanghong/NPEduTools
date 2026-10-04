# 关于页面 · 2026-10-03

## 本轮交付

主窗口左侧导航新增「关于」，位于「设置」下方。在主窗口内浏览，无需另开窗口。

参考 NPClassworks 的关于页面结构，采用 NPEduTools 现有绿色主题、矢量标志与卡片样式，包含：

- 产品介绍、当前程序版本及开发预览状态。
- 星火动力介绍、官网 `https://novark.ink/`，以及 NPClassworks / KV 项目入口。
- NPEduTools 源码、发布下载、问题反馈入口。
- 本项目 GPL-3.0 许可及第三方材料入口；未沿用 NPClassworks 的 AGPL 许可或派生关系描述。
- ClassIsland、ExamAware2、SecRandom、FFmpeg、NAudio、.NET / WPF 致谢与项目链接。
- 可复制的版本信息：程序集版本、运行时、进程架构和构建基准。Debug 构建提示可能存在未提交改动。

## 实现入口

- `src/NPEduTools.App/AboutPage.xaml`：页面布局、卡片与链接。
- `src/NPEduTools.App/AboutPage.xaml.cs`：读取程序集信息、打开固定 HTTPS 链接、复制版本信息。
- `src/NPEduTools.App/MainWindow.About.cs`：关于页导航。
- `MainWindow.xaml` / `MainWindow.xaml.cs`：导航按钮与页面可见性切换。

页面初始化只读取程序集信息，不请求学校服务，不读配对凭据；链接仅在用户点击后交给默认浏览器打开。打开链接和复制失败时在页面内提示。

## 验证

- `dotnet build src/NPEduTools.App --no-restore -p:UseSharedCompilation=false -v:q`：通过，0 警告、0 错误。
- 静态检查：关于页与概览、设置、快捷启动页面互斥；导航选中状态和面包屑同步。
- `git diff --check`：通过。
- 尚待 IDE Debug 人工观察：最小窗口尺寸和高 DPI 布局、页面滚动、默认浏览器跳转与剪贴板复制。

本轮未更新版本号、未打包、未提交或推送，也未修改 NPClassworks / KV。官网内容未能通过浏览工具读取；介绍参考本地 NPClassworks 关于页，官网仅作为用户指定的入口。
