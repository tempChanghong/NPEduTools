# C30 / Datedu 第一轮静态分析

分析日期：2026-09-17。样本目录：`D:\WebstormProjects\Datedu`。

本轮读取目录、配置、部署脚本、JavaScript、Protobuf 和 PE 元数据，并比较两个版本的部分二进制文件。没有启动样本程序、加载其 DLL、请求其云端接口或修改样本。结果用于理解架构与后续本地集成研究，不构成完整源码恢复或运行行为验证。

## 主要结论

C30 是多进程 Windows 教学软件。可确认的组成包括 Go 启动程序、以 x86 C++ / MFC / DuiLib 为主的教学组件、CEF 浏览器进程、Vue 等网页模块、Nginx / Node.js 本地服务，以及局部 .NET PowerPoint 插件。仅凭 CEF 与 Node 文件并不能将其归类为 Electron。

目录同时保存授课版本 `1.3.1568.0` 和 `1.3.1610.0`。`teach/AppData/launcher.ini:2-3` 的 `last_start_verion`、`new_verion` 都指向 `1.3.1610.0`，因此本轮以该版为主；这是磁盘配置记录，不代表本机当前运行状态。

这里包含部署程序、第三方资源、缓存、临时图片、日志与设备状态，并非只含编译输出。含隐藏文件共 **34,789 个文件、5,206,357,375 字节（约 4.85 GiB）**；普通非隐藏文件为 34,781 个。包含 7,602 个 JS、463 个 HTML、888 个 DLL、95 个 EXE、597 个 `.log`。这些数量包括两版重复与依赖，不能直接视为业务代码规模。

## 目录与功能

| 路径（相对 Datedu） | 观察到的职责或内容 |
| --- | --- |
| `teach/TeachLauncher.exe` | 授课启动器；版本资源标注 C30 智能教学 1.3.1610.0 |
| `teach/1.3.1610.0/teach/teachingtools` | 教学主程序、白板、Office、媒体、网页及公共 DLL |
| `teach/1.3.1610.0/teach/server/cls` | `cls.exe`、课堂数据 Protobuf 定义及 PDB |
| `teach/1.3.1610.0/teach/server/rs` | 资源/传输服务组件及 TCP、HTTP、RTP 配置 |
| `teach/1.3.1610.0/teach/server/nginx` | 静态资源与代理配置 |
| `teach/1.3.1610.0/teach/server/nodeweb` | 可直接阅读的 Express 服务代码和依赖 |
| `znbk/apps/assisttool` | 备课相关原生程序、CEF、资源管理及网页 |
| `znbk/addins` | Office Ribbon XML |
| `znbk/huohua/huohua_addin` | .NET PowerPoint 插件及 NetOffice 等依赖 |
| `teach/AppData`、根目录 `AppData` | 启动版本及应用状态；须结合各模块配置区分实际使用路径 |
| `Device_Info_`、`Logs`、各级日志和缓存目录 | 设备与历史运行材料；本轮未整理个人记录或会话内容 |

## 程序入口与原生组件

直接解析了 17 个代表性 PE 的头、导入表及命名导出表，并保存 SHA-256。所有抽样文件的 Machine 均为 `0x14c`；其中 16 个无 CLR 目录，`Xiyue.Huohua.PPT.dll` 有 CLR 目录。对托管 DLL，`0x14c` 本身不足以判断是否只能在 x86 进程运行，仍需检查 CLR 标志。

| 文件 | 可核查证据 | 判断 |
| --- | --- | --- |
| `TeachLauncher.exe` | `Go build ID`、`runtime.main`、`runtime.goexit`、`go1.18` 字符串 | Go 启动器 |
| `teachingtools_teach.exe` | 相同 Go 标记及 `go1.14.3` 字符串 | 另一 Go 可执行入口；与启动器、主程序的精确调用顺序待确认 |
| `teach.exe` | 导入 `teach_core.dll`、`mfc110u.dll`、`DuiLib.dll`、`http_svc.dll` | 原生教学主程序 |
| `teach_core.dll` | 导入 `js_core.dll`、`base_app_teach.dll`、`net_interact.dll`；44 个命名导出 | 教学功能组织层 |
| `js_core.dll` | 35 个命名导出，含 `DT_JSCore_Init`、`DT_QueryJSCoreModuleByName`、`DT_JSCore_CallJsMsg` | 原生 JS 模块与消息组织层 |
| `soft_cefclient.exe` | 直接导入 `libcef.dll` | CEF 浏览器进程组件 |
| `live_office.dll` | `CreateOfficeConvertor`、`CreatePPTAddInConvertor`、`CreateWordConvertor` 等导出 | Office 转换组件 |
| `STAssistantWorkers.exe` | 导入 DuiLib、js_core、SQLite 等；版本 1.3.1610.0 | 备课侧原生组件 |
| `Xiyue.Huohua.PPT.dll` | CLR 目录、`mscoree.dll` 导入、版本 2.2.1.11111 | 独立托管插件 |

`teach_core.dll` 还导出 `DT_CreatePPTControl`、`DT_CreatePPTPageWnd`、`DT_CreatePPTThumbnailWnd`，可作为后续研究 PPT 控制与缩略图功能的定位点。导出名不能确定参数、调用约定、对象生命周期或外部调用稳定性。

`teachingtools/programlist.ini` 枚举 rs、nginx、nodew、投屏播放器、录制程序、桌面程序和 teach.exe。它提供进程组成线索，不能单独证明启动顺序，亦不能证明列表中的程序会同时运行。

## 网页与原生程序如何通信

旧式封装位于 `teachingtools/public_web/assets_js/tools.js:1-22`：

```text
call_client(tag, msg)
  → call_cplus('mirco.call_cplus', tag, msg)
  → cef.message.sendMessage(cmd, [msg, tag])
```

对象式封装位于 `teachingtools/public_web/vue_projects/mainProject/mainProject/libs/cefInject/win_core.js:1`：

```text
wincore.<module>:<sync|async>:<method>:<uuid>
  → cef.message.sendMessage(methodId, [payload, callbackTag])
  ← wincore.onCallback / wincore.onAyncNotify
```

该脚本定义 24 类模块，包含 `System`、`Disk`、`BrowserForm`、`WindowForm`、`InkView`、`WBForm`、`EBookForm`、`SQLite`、`HttpUtils`、上传下载和音视频模块。词法提取得到 **150 个去重方法名**；该统计仅覆盖显式 `this` 形式的桥接调用，并非完整的模块与方法对应表或受支持 SDK。

这些能力依赖 CEF 宿主提供的 `cef` 对象、原生模块初始化和对象 UUID。由此可推断，单独用普通浏览器打开页面不足以还原完整应用功能；网页桥接也不等于外部进程可用的公开 IPC。

`mainProject` 保留 Web Worker、脚本注入、消息、版本检查、基础信息等辅助 JS。`webworker/localservice.class.js:1` 通过 `BroadcastChannel` 的 `local_service` 通道调度、使用 IndexedDB 保存班级和设置，并在部分情况下请求业务服务。这里的 LocalService 是网页内部模块，并不能按名称认定为独立 HTTP 服务。

`public_web/vue_projects` 有 29 个一级项目目录，可见登录、桌面、课堂资源、作业、限时练习、互动报告、拍照讲解、微课编辑、点赞、观点云等。`mainProject/index.html` 引用 app/chunk 脚本及共享 DLL bundle；另有 Vue、Axios、jQuery、Fabric、Socket.IO 等资源。

## 本地服务与数据

| 证据位置 | 文件内容体现的行为 | 验证边界 |
| --- | --- | --- |
| `server/nodeweb/bin/www:15,28` | 默认 `PORT` 为 9022，`server.listen(port)` 未指定主机 | 环境变量可改变端口；未测实际监听 |
| `server/nodeweb/app.js:25-31` | `/userdata`、`/upload`、`/cls`、`/h5res` 等静态映射 | 目录是否存在、是否被现版本使用待确认 |
| `server/nodeweb/routes/dynamic.js:6-32` | 动态注册静态目录；注册入口检查请求 IP 为回环地址 | 未调用注册或读取接口 |
| `server/nginx/conf/nginx.conf:47` | `listen 9022`；后续包含静态资源及代理映射 | 与 Node 默认端口重合；可能运行时改写或选择使用，尚未证明 |
| `server/rs/conf/rs.cfg` | RS TCP 19561、HTTP 19560、RTP 19000；DS 配置为 8010/8011 | 配置项不等于已启用监听 |

Nginx 配置使用 `../../../../AppData/teach/...` 等相对路径，Node 代码中的部分路径仍指向版本目录下的 `../cls`。路径布局差异说明不能把两者当作完全等价服务；必须先确认启动参数、工作目录与运行时生成配置。

`server/cls/cls.proto` 保留 15 个 message 定义，使用 `required`、`optional`、`repeated` 字段语法：

| 数据类别 | 代表消息 |
| --- | --- |
| 班级成员 | `UserInfo`、`UserList` |
| 题目及题面 | `QuestionPb`、`QuesSurfaces`、`QuestionInfoPb` |
| 学生作答 | `AnswersPb`、`StuAnswerPb` |
| 选项及统计 | `OptionPb`、`QuesDetailsPb`、`CalcInfoPb`、`CalcPb` |
| 分组、互评、点赞 | `GroupsPb`、`ReCorrectInfo`、`LikedStudent`、`LikedPb` |

这些字段提供了名单→题目→作答→统计的结构关系。文件不包含完整传输帧、命令号和全部业务校验。`cls.pdb` 提供潜在调试符号材料，本轮仅确认存在，未验证与 EXE 的 GUID/age 匹配或私有符号完整性。

`teachingtools/teach.ini` 配置 `screenservice.iclass30.com`、`screensocket.iclass30.com`、`fs.iclass30.com`、`recordlog.iclass30.com` 等主机，分别提供业务、实时消息、文件和事件记录的线索。这里仅归纳磁盘配置，未验证这些服务目前的行为。配置中也含开发/生产变体；不将其混合视为当前环境。

## 两版比较

比较范围严格限于两版 `teachingtools` 顶层的 `.exe` / `.dll`，以完整 SHA-256 判定同名文件是否相同；未比较整个部署树。

| 结果 | 数量 |
| --- | ---: |
| 内容相同 | 148 |
| 同名但内容变化 | 40 |
| 新增 | 2 |
| 删除 | 1 |

新增 `vk_swiftshader.dll`、`vulkan-1.dll`，删除 `d3dcompiler_43.dll`。变化包括教学核心、JS 核心、界面、媒体程序、CEF 等；哈希变化不等于业务逻辑变化，可能包含编译或签名差异。

CEF 文件版本由 `3.3626.1892.g7cb6de3` 变为 `109.1.18+gf1c41e4+chromium-109.0.5414.120`；后者明确标出 Chromium 109。这是本轮确认的一项主要底层升级。Nodew 的文件版本为 `6.2.0`；package.json 声明 Express `~4.16.0`。这两处分别是运行时文件版本和依赖范围，不能混称实际运行中的依赖版本。

全目录找到两个 `.pdb`（两版 cls），两个 `.map`（两版第三方 source-map 库），五个 `.proto`。没有找到可直接用于恢复主业务 Vue/TypeScript 源码的独立 `.map` 文件；尚未全面检查 bundle 内嵌 source map。

## 后续研究入口与集成判断

1. **启动与窗口控制**：从 TeachLauncher 的版本选择、工作目录和启动参数入手。结合现有 NPEduTools，普通程序启动和窗口识别是更容易形成独立集成的一层，但尚未验证具体行为。
2. **PPT / 白板状态**：先研究 `DT_CreatePPTControl` 对应调用点及 `wincore` 的状态与事件流，确认是否存在可供外部使用的接口，再决定适配方式。当前证据不足以直接写入现有 PPT 触摸辅助实现。
3. **课堂数据结构**：从 `cls.proto` 和可阅读的 JS 消费代码建立字段字典；若需样例，用人工构造数据。消息结构比先反编译所有 EXE 更明确。
4. **运行时拓扑**：在后续受控运行中记录进程树、命令行、监听地址与端口，解决 Node/Nginx 9022 重合及相对路径归属。当前没有开展这一步。
5. **深层二进制分析**：按实际研究目标选择 teach_core、js_core 或 cls；匹配调试符号、确定调用关系后再分析函数。无需先处理所有第三方 DLL。

本轮未发现已能确认的公开、稳定、供第三方直接使用的 C30 SDK 或 IPC 协议。这个结论只表示尚未找到证据，不代表产品没有此类接口。没有进行漏洞验证、授权机制分析或服务端实现推测。

## 本地分析产物

以下文件保存在 NPEduTools 的 `.artifacts/datedu-analysis/`，被现有 Git 忽略规则排除：

- `analyze.py`：只读扫描脚本；输入为固定 Datedu 路径，输出写入脚本所在目录。
- `inventory.json`：数量、扩展名、顶层目录体积及调试文件位置。
- `pe-metadata.json`：17 个样本的 SHA-256、架构、CLR 标记、导入及命名导出。
- `version-diff.json`：限定范围的两版二进制比较。
- `bridge-index.json`：桥接模块与去重方法名索引。

复跑命令：`python .artifacts/datedu-analysis/analyze.py`。脚本仅使用 Python 标准库，是针对当前样本的分析辅助工具；PE 解析未覆盖延迟导入、序号导出、CLR 标志与完整符号解析。
