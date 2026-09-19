# NPEduTools ExamAware 桥接 0.1.0

适配 Windows ExamAware2 **1.5.2**，官方 Plugin API V2 / SDK 1.5.2。第一阶段仅报告应用版本、平台、发行状态及原生登录自启动的 `openAtLogin` 登记值，不读取考试正文，不提供退出或自启动修改命令。

## 安装与连接

1. 在 NPEduTools 左侧打开「考试看板」，选择安装目录中的 `ExamAware.exe`，点击「保存位置」。
2. 点击「打开插件设置」，通过 ExamAware 自带的本地插件安装功能导入 `npedutools-examaware-bridge-0.1.0.ea2x`，检查并授权插件声明的权限，启用插件。
3. 在 NPEduTools 点击「导出配对文件」。
4. 回到 ExamAware **主页面**，点击插件新增的「连接 NPEduTools」，选择刚导出的 JSON。
5. 回到 NPEduTools，确认「桥接已连接（只读）」。导出的文件含配对凭据，导入后可以删除。

插件通过官方 `ctx.api.network.connectTcp` 反向连接本机，不需要开启 ExamAware 内置 HTTP 服务。无需管理员权限。配对文件必须来自同一台计算机上正在使用的 NPEduTools 实例。主程序和 Host 重启后沿用原端口及配对信息，插件会自动重新连接。

「已发送打开请求」只表示操作受理，并不证明窗口已经显示或桥接就绪。侧栏的「打开考试看板」使用已保存的位置；尚未配置或打开失败时转到管理页面。

## 状态含义

- **尚未连接**：没有配对、插件未启用、应用未运行或连接已失效。自启动显示「未知」。
- **桥接已连接（只读）**：取得带认证的近期状态，目标平台和版本符合本阶段适配范围。
- **已登记 / 未登记**：读取 `getAutoStart()` 的结果。任务管理器是否禁用该启动项仍需在 Windows 中确认。
- **连接服务不可用**：配置损坏、同目录已有后台或固定配对端口被占用。原文件保留；排除原因后重启后台。
- **版本尚未验证**：连接存在，但不是 Windows 1.5.2；不将该版本的自启动值显示为有效状态。

在 ExamAware 主页面点击「断开 NPEduTools」可清除插件内的配对信息。关闭 NPEduTools 不会退出 ExamAware。插件被禁用或卸载时会释放连接和定时器。

## 构建与验证

仓库根目录执行 `./scripts/build-examaware-bridge.ps1`。脚本先固定依赖、检查 TypeScript、生成主进程 CJS/渲染进程 ESM，运行插件与真实 .NET Host 的跨进程测试，然后生成 `.artifacts/examaware-bridge/npedutools-examaware-bridge-0.1.0.ea2x`。需要 Node.js 24、npm 11、项目 .NET SDK 和已还原的 NuGet 依赖。

独立开发：在本目录执行 `npm ci --ignore-scripts`、`npm run build`。`npm test` 需要先构建仓库的 Release Host；使用独立的测试管道和临时目录，未安装或启动 ExamAware 本体。

已经完成构建与验证、仅需重打包现有产物时，可使用 `./scripts/build-examaware-bridge.ps1 -PackageOnly`；该模式不会重新运行编译与测试。

SDK 1.5.2 的 npm 包包含 `workspace:*` 依赖，根包使用 overrides 固定到相同源码标签中的 `@dsz-examaware/core` 1.1.1 / `@dsz-examaware/rpc` 0.3.0。渲染端打包官方 SDK 的生命周期代码；主进程 SDK 由 ExamAware 宿主解析。没有替换或修改官方 SDK 源文件。

**验证边界**：已做编译、官方 SDK 入口和模拟宿主 API 下的 TCP/命名管道联调；尚未完成官方 ExamAware2 发行版安装插件、窗口唤起及登录启动的实机验收。此包为第一阶段联调版本，不是已完成全部兼容性验收的发行版。

实现与协议说明见仓库 `docs/EXAMAWARE2-STAGE1.md`。
