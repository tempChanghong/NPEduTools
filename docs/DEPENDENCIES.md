# 原型依赖说明

版本由项目文件和各项目的 `packages.lock.json` 固定，验证时使用 `--locked-mode`。升级需同时更新锁文件并运行实际 IPC 回归测试。

| 依赖 | 固定版本 | 用途 |
| --- | --- | --- |
| .NET SDK | 10.0.400 | 编译、运行和测试 |
| ClassIsland.Shared.IPC | 2.1.0.1 | 官方远程课程服务契约及客户端 |
| dotnetCampus.Ipc | 2.0.0-alpha410 | 与 ClassIsland 保持一致的通信库及代理生成器 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 测试运行器 |
| xunit | 2.9.3 | 测试框架 |
| xunit.runner.visualstudio | 3.1.4 | 测试适配器，仅用于测试 |
| NAudio.Wasapi / NAudio.Core | 2.2.1 | WASAPI 音源采集、缓冲和采样率转换，MIT |
| FFmpeg / ffprobe（Gyan essentials） | 9.0.1 | 独立录制进程使用的 GDI、x264、AAC、MKV/MP4 与验证工具；该构建为 GPL v3 |

ClassIsland.Shared.IPC 的 NuGet 包元数据声明 `LGPL-3.0-only`，并依赖同版本 `ClassIsland.Shared`。dotnetCampus.Ipc 使用带 alpha 后缀的固定版本，这是目标 ClassIsland 发布使用的依赖；并非声称所有依赖均为无预览后缀版本。

本阶段通过 NuGet 引用程序集，没有复制或修改上游实现源码。所有生产分发准备工作，包括完整依赖许可清单、必要声明及对应源码获取方式，应在打包阶段核对。参考文档的许可独立于项目根目录许可证。

源码与包来源：

- [NAudio 源码及 MIT 许可证](https://github.com/naudio/NAudio)，使用 NuGet 锁定 2.2.1。
- [FFmpeg 官方下载](https://ffmpeg.org/download.html)列出的 [Gyan Windows 构建](https://www.gyan.dev/ffmpeg/builds/)。`scripts/bootstrap-recorder.ps1` 固定 ZIP SHA-256：`FEC81AE03971D9DD4BE3EBE02E263BD2EC1D789483F931BDBA5F5715E65DA2E9`。Tools 保留上游 `LICENSE`、`README.txt` 和本地校验清单。上游构建说明指向 [FFmpeg 源码提交 bf1b838f2a](https://github.com/FFmpeg/FFmpeg/commit/bf1b838f2a)；生产分发时仍需整理完整的构建依赖及对应源码材料。

- [ClassIsland 2.1.0.1 发布](https://github.com/ClassIsland/ClassIsland/releases/tag/2.1.0.1)
- [ClassIsland.Shared.IPC 2.1.0.1](https://www.nuget.org/packages/ClassIsland.Shared.IPC/2.1.0.1)
- [dotnetCampus.Ipc 2.0.0-alpha410](https://www.nuget.org/packages/dotnetCampus.Ipc/2.0.0-alpha410)
- [微软 .NET 10 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
