# ExamAware2 1.5.2 真实源码宿主联调

日期：2026-09-19。当前桥接插件：0.1.1。

## 结论

用户提供的 `D:\WebstormProjects\ExamAware2` 是源码仓库，原先没有可运行发行包。已构建并运行其中的 ExamAware2 1.5.2，使用其真实插件安装器、API V2、SDK 权限和 TCP 实现，连接独立 NPEduTools Host。11 项宿主检查通过，6 项插件回归测试通过。

实测发现并修复了 0.1.0 的安装后加载故障。当前应使用 `npedutools-examaware-bridge-0.1.1.ea2x`。本次没有添加退出命令或自启动写入功能。

这不是官方发行版 EXE 的完整验收：运行的是官方源码构建结果，由 Electron 开发运行时承载，`app.isPackaged=false`。未放宽 NPEduTools 生产环境对 `ExamAware.exe`、版本及 `resources/app.asar` 的检查。

## 版本、构建与隔离

| 项目 | 实际值 |
| --- | --- |
| 源码目录 | `D:\WebstormProjects\ExamAware2` |
| 提交 | `7979213fed918eaece7a5bf424e15f534778d7f2` |
| Desktop / Plugin SDK | 1.5.2 / 1.5.2 |
| Electron | 39.2.7 |
| 构建工具 | Node 24.14.1，仓库指定 pnpm 10.18.2 |
| NPEduTools Host | 现有 .NET 10 Release 后台，独立命名管道与数据目录 |

按锁文件使用 pnpm 标准安装，先跳过安装脚本，再执行所需 esbuild 构建准备及 Electron 官方安装入口。分别构建 rpc、core、player、plugin-sdk、control-protocol，最后构建 desktop。初次遗漏 control-protocol 导致桌面渲染构建失败；补建后成功。上游存在大包体积提示，不影响本次构建完成。源码仓库没有被修改；依赖和产物位于其忽略目录。

可重复测试在系统临时目录创建唯一夹具，在导入宿主主入口之前设置 `userData`、`sessionData` 和日志位置。测试结束关闭自建进程并删除夹具、配对密钥和测试配置，报告仅保留不含密钥的结果及界面截图。

隔离适配明确包含：

- 拦截 `setAsDefaultProtocolClient`，防止改变系统协议关联。因此验证的是第二实例命令行中的深链接，不是系统 URL 注册。
- 对 `setLoginItemSettings` 设置禁止写入的断言；实测写入尝试为 0。`getLoginItemSettings` 保持原生读取。
- 原生文件选择框返回指定的测试文件；原生结果提示框自动记录并关闭。渲染界面的权限说明、勾选确认及安装器没有替换。
- 不模拟 `ctx.api`、插件加载器、配对设置存储或网络。主页面插件按钮通过实际界面点击。

## 发现的问题与修复

0.1.0 主进程 bundle 保留 `require('@dsz-examaware/plugin-sdk')`，期望由宿主解析。真实安装器把插件放在 `userData/plugins/npedutools-examaware-bridge-0.1.0` 后，加载失败，报告 `Cannot find module '@dsz-examaware/plugin-sdk'`。

宿主 `packages/desktop/src/main/plugins/loader.ts` 虽然用 `Module.createRequire` 加载插件，但插件内部的 `require` 仍以插件所在位置搜索依赖。开发目录中的 `node_modules` 掩盖了此问题，之前的模拟 API 测试没有覆盖实际安装位置。

修复仅作用于 NPEduTools 插件：移除主进程构建中的 SDK external 配置，将所需官方 SDK 代码打入 CJS；版本提升至 0.1.1，同步锁文件、说明及第三方声明。没有修改官方 SDK 或 ExamAware2 源码。

新增 `installed-entry.test.mjs`：将主入口复制到独立临时目录，通过干净子进程加载，清除 `NODE_PATH` 与 `NODE_OPTIONS`，检查入口函数和 API V2 标识。它防止构建再次依赖开发目录中的 SDK。

## 检查结果

| 检查 | 结果 |
| --- | --- |
| 真实源码宿主启动、隔离目录、版本 | 通过，1.5.2、非打包模式 |
| 实际 ea2x 安装、TCP 权限确认、入口及主页按钮 | 通过，未勾选时不能确认安装 |
| 主页导入配对、SDK TCP、认证状态读回 | 通过，1.5.2、自启动登记 false，与原生读取一致 |
| 插件重载 | 通过，连接恢复，按钮无重复 |
| 禁用再启用 | 通过，禁用后断开、状态未知、按钮移除；启用后恢复 |
| NPEduTools Host 重启 | 通过，端口与配对凭据保留，插件重新连接 |
| ExamAware 重启 | 通过，插件及配对设置恢复 |
| 主窗口隐藏后再次启动 | 通过，真实第二实例退出，原实例窗口重新显示 |
| 第二实例设置深链接 | 通过，插件设置和基本设置均打开 |
| 用户断开后重新导入 | 通过，断开时读数未知，重新导入后连接 |
| 写入隔离断言 | 通过，自启动写入尝试为 0；协议注册被拦截 |

此外 `npm test` 的 6 项回归测试通过，包括新增独立安装入口检查和原有认证、错误密钥、设置变化、生命周期及权限声明检查。此前 288 项 .NET 和 3 项 WPF 结果为上一阶段记录，本次未修改 .NET/WPF 代码，也没有将其记为重新全量运行。

首轮完整真实宿主结果：`.artifacts/examaware-real-host/2026-09-19T15-31-00-838Z/summary.json`。同目录包含 `permissions.png` 和 `connected-home.png`；后续重跑生成独立时间戳目录。

最终交付包重新构建后复测同样为 11/11，通过记录位于 `.artifacts/examaware-real-host/2026-09-19T15-34-13-230Z/summary.json`。Release Host 重新构建为 0 警告、0 错误。最终 `npedutools-examaware-bridge-0.1.1.ea2x` SHA-256：`D5A587B5CFD27F8E79B3880D34ADAEDD87A66EB725CE600E2CC03A9CC688E10D`。

正式自动测试的临时夹具已清理，测试进程均已结束。此前人工探索使用的 `.artifacts/examaware-real-host/1789831524587` 仍保留测试配置与一次性配对信息；收尾清理命令被自动审批审查拒绝，仅返回“blocked by policy”，未提供更具体理由，因此没有换一种执行方式重试。探索期间项目根目录另出现 `%SystemDrive%/ProgramData/SogouInput` 缓存目录，未纳入代码或插件包，也未删除。

## 复现方法

先在 ExamAware2 仓库完成依赖安装以及上述工作区包和桌面构建。在 NPEduTools 仓库执行：

```powershell
./scripts/build-examaware-bridge.ps1
node scripts/test-examaware-host.mjs --source D:/WebstormProjects/ExamAware2 --playwright <本机playwright包的package.json绝对路径>
```

`--playwright` 可省略，前提是当前 Node 环境可以解析 Playwright。测试不需要启动或关闭用户的正式 ExamAware 实例。不要把源代码目录或 `electron.exe` 当作 NPEduTools 管理页的正式程序位置。

## 尚待验证与下一阶段

需继续使用官方 1.5.2 发行版，在隔离环境验证 NPEduTools 对真实 `ExamAware.exe` 的路径校验、启动、第二实例唤起、不同安装目录拦截及程序移动后的提示。源码宿主开发进程的自启动登记值不能代表官方发行版 EXE 的登记状态；本次没有执行注销登录或真正的开机测试。

本轮仍处于 E1/E2 验收。完成发行版检查后，按原计划推进 E3 正常退出，再推进 E4 自启动配置。加入写操作前仍需完善双方挑战握手、请求应答、操作期限与配对撤销；不得把当前只读连接直接扩展成无约束远程指令通道。

火绒事件仍按上一阶段记录处理，未获得厂商误报复核结论。本次没有恢复被删除的缓存修复脚本，也没有修改杀毒软件设置。
