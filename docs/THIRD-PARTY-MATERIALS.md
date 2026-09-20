# InDev 20260920 第三方材料

本版程序包不附带 FFmpeg、ffprobe 及其二进制 ZIP。录制组件由使用者按 `RECORDING-TOOLS-INSTALL.md` 直接从 Gyan 上游下载，版本、归档及可执行文件哈希固定在 `recording-tools.json`。本项目不镜像该二进制；安装后的个人目录不要当作本版发行包重新上传。

## 随包材料

- `third-party/inventory.json`：由实际发布的 `.deps.json` 生成的 NuGet 组件清单；各组件目录保留 NuGet 元数据与自带声明。
- `third-party/licenses/`：补齐 NuGet 未携带的上游许可证和 WPF 第三方声明。`licenses.lock.json` 记录固定提交的原文地址及 SHA-256，不修改原文。
- `.NET Runtime` 和 `WindowsDesktop Runtime` 10.0.11 的许可证、运行时第三方声明及 NuGet 元数据保留在对应目录。
- `third-party/sources/`：ClassIsland 与 ExamAware 对应提交的完整仓库源码归档、构建文件和锁文件。`sources.lock.json` 记录来源、提交、长度及哈希。这些文件同时作为 `NPEduTools-InDev-20260920-third-party-sources.zip` 单独提供，程序包内已包含，不必重复下载。
- `NPEduTools-source.zip`：本项目源码、插件源码、构建脚本及依赖锁文件。程序未对下表的上游库实现作本地修改。

## 组件映射

| 发布组件 | 许可 | 材料与来源 |
| --- | --- | --- |
| ClassIsland.Shared / Shared.IPC 2.1.0.1 | LGPL-3.0-only | NuGet repository commit `15273f82c9d2d55929df83b5fb806e68ee4547c0`；对应源码归档中的两个同名项目、Global.props、GeneratePackage.props、Protobuf 定义和各自 LICENSE.txt |
| ExamAware plugin-sdk 1.5.2 | GPL-3.0-only | npm gitHead `7979213fed918eaece7a5bf424e15f534778d7f2`；对应归档的 packages/plugin-sdk、workspace 配置及 pnpm 锁文件 |
| ExamAware core 1.1.1 / rpc 0.3.0 | GPL-3.0-only | npm gitHead 分别为 `d27cfebf7f5becbd4b08ba30b4241441328dc91a`、`ee1dee1b9e4912099d506a16547f35604e736d23`；作为 SDK 关联源码补充保留，不能将三者误写成同一发布提交 |
| CommunityToolkit.Mvvm 8.2.1、CsesSharp 1.0.0、dotnetCampus.Ipc 2.0.0-alpha410 | MIT | NuGet 元数据与组件许可；dotnetCampus 的 nuspec 未填写 license 字段，以锁定提交的 LICENSE 为准 |
| Google.Protobuf 3.27.0-rc1 | BSD-3-Clause | 上游 LICENSE |
| Grpc.Core.Api / Net.Client / Net.Common 2.71.0 | Apache-2.0 | 上游 grpc-dotnet LICENSE，三个组件共用 |
| Microsoft.Extensions 八个 8.0.0 组件 | MIT | 各 NuGet 自带 LICENSE.TXT 及第三方声明；精确名称见 inventory.json |
| NAudio.Core / Wasapi 2.2.1 | MIT | 上游 v2.2.1 对应提交 `b5d5ff83fd378f046398891fe5cd99426ce44732` 的 license.txt |
| Newtonsoft.Json 13.0.2 | MIT | NuGet LICENSE.md |
| YamlDotNet 16.3.0 | MIT | 上游 LICENSE.txt 和 LICENSE-libyaml，两者均保留 |
| .NET / WindowsDesktop Runtime 10.0.11 | MIT 及内含第三方声明 | 自包含运行时包的许可证、.NET 第三方声明、WindowsDesktop 对应源码提交的 WPF 声明 |

本项目许可证为 GPL-3.0。ClassIsland 契约库保持独立 DLL，本包不禁止用户为修改、调试这些库进行替换或重新构建；注意保持接口兼容。插件使用宿主已有的组件，不将 ClassIsland 或 ExamAware2 本体打入包中。

ExamAware 插件内的 `dist/bundle-inputs.json` 记录 esbuild 实际输入。当前两个产物的第三方直接输入均为 plugin-sdk/dist/index.mjs；npm 安装时的 CLI、打包工具及可选 Electron/Vue 不因此被视为随插件分发。SDK 自身的上游实现与构建依赖由完整源码归档保留。

## 构建与替换

本项目：Windows 上安装 `global.json` 指定的 .NET SDK 10.0.400 和 Node.js；执行 `scripts/dotnet.ps1 restore NPEduTools.sln --locked-mode`，随后执行 `scripts/package-npedutools.ps1`。打包过程使用提交的 NuGet/npm 锁文件，下载并校验第三方源码，不调用 FFmpeg 引导脚本。完整自包含发布所用锁文件另见 `build-locks/`。

ClassIsland：解开 ClassIsland 源码归档，在其根目录安装上游 global.json 所要求的 SDK，然后执行 `dotnet build ClassIsland.Shared.IPC/ClassIsland.Shared.IPC.csproj -c Release -f net8.0`，会同时构建 Shared。项目保留包版本及代码生成设置，所需 NuGet 包按项目声明恢复。仓库内 EdgeTtsSharp 子模块属于宿主语音功能，这两个契约项目不引用它；本包不包含它的二进制。替换时退出 NPEduTools，在引用这些库的应用/Host 目录备份并替换同名 DLL，或者修改本项目引用后整体重建。

ExamAware SDK：解开 plugin-sdk 对应完整仓库，按根 package.json 的 packageManager 使用 pnpm，执行 `pnpm install --frozen-lockfile`，再执行 `pnpm --filter @dsz-examaware/plugin-sdk build`。保留 packages/core、packages/rpc 和工作区构建文件；另外两个归档对应 npm 已发布依赖的各自版本。NPEduTools 插件在其项目目录执行 `npm ci --ignore-scripts`、`npm run build`；脚本 `scripts/build-examaware-bridge.ps1` 完成回归和 .ea2x 打包。可将修改后的 SDK 构建产物用于本地依赖后重新生成插件。

源码归档保留上游构建方法；本次不声称可将上游 NuGet/npm 制品逐字节复现。已验证的范围是固定版本/提交映射、源码与构建输入存在、哈希完整性以及本项目的构建与回归。

## 获取与维护

[本版本发布页](https://github.com/tempChanghong/NPEduTools/releases/tag/InDev-20260920) 提供项目源码和第三方源码附件，公开时必须和二进制同时提供。程序 ZIP 也直接携带这两类材料。维护者可运行 `scripts/collect-third-party-sources.ps1 -OutputDirectory <目录>` 重新收集并核验；不得以 GitHub 自动生成的本项目 Source code ZIP 替代第三方源码。

升级依赖后须重新核对 actual deps、bundle inputs、许可证和源码锁定信息。此前包含 FFmpeg 的候选包已被本版分离安装方案取代，不应再用于公开发布。
