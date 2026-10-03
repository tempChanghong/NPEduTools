# 下一版本：内置录制组件草稿

2026-10-02 · 下一版本号、代号及发布渠道待定。**下一版本计划内置 FFmpeg 和 ffprobe，完整解压后即可录课，不用另外安装组件。InDev 20261002 与网页／KV v1.2.0 已发布，其标签、附件和说明保持不变。**

本轮只整理录制组件、构建／打包和材料说明；学校互联、通知、考试模式、噪音监测与排程沿用 InDev 20261002。配套网页／KV 后续安全修复以实际提交组合为准。

此前生成的同名本地 ZIP 仅是构建试验，不是已发布版本的替换附件，也不是已确定版本号的新发布。新打包必须显式指定新版本号和本文路径；默认不会再生成旧版本包。原始构建材料中的打包示例也应补上这两个参数。

> [!CAUTION]
> 开发预览版本，真实班级大屏长课、驱动、双音源和重启／休眠仍待验收。新组件通过合成媒体检查不代表真实屏幕／麦克风录制已验收。保留手动操作与备用方案；ExamAware 编辑器退出问题仍按既定决定暂缓。

## 使用

完整解压主 ZIP，运行 `Start-NPEduTools.cmd`，核对并允许启动 UAC。在“录制微课”选择屏幕、音源、保存目录，先短录并回放。**不要再运行旧版 Install-Recording-Tools.ps1，不要覆盖旧的 Gyan EXE。** 本候选两处录制器都带组件，手动和自动录课共用同一构建版本；不修改系统 PATH。

FFmpeg 是录课组件；ClassIsland 和 ExamAware 本体仍需自行安装，并通过随包插件连接。自动录课与噪音排程仍需可靠 ClassIsland 学校时间。噪音监测只上传统计，不保存或上传原录音；dBFS 不是校准声压级。

## 材料与检查

| 文件／目录 | 用途 |
| --- | --- |
| `app/Recorder/Tools/`、`app/Host/Recorder/Tools/` | 内置录制组件、原许可、说明与哈希清单 |
| `third-party/ffmpeg/NPEduTools-recording-tools-source.zip` | FFmpeg／x264 精确源码归档、许可证、全部构建脚本、配置和工具链记录；用户录课无需操作此文件 |
| `recording-bundle.lock.json` | 已验证构建的文件与源码归档哈希 |
| `NPEduTools-source.zip`、`third-party/`、`build-locks/` | 本项目构建源码快照、其他第三方材料及依赖锁 |
| `package-manifest.json`、`verify-portable-package.ps1` | 包内文件、运行时、两处录制组件和对应源码的完整性检查；校验器使用 PowerShell 7 |
| `README.md`、`CLASSROOM-ACCEPTANCE.md` | 安装更新与真实大屏验收说明 |

专用构建采用 FFmpeg 9.0.1 与 x264 0.165.r3222，启用 GPL/version3，没有 nonfree 或其他外部媒体库。保留 GDI、浮点 PCM、x264 H.264、AAC、缩放、MKV、concat、MP4 和成片解码；不包含网络、GPU 编码或通用 FFmpeg 的全部功能。现有录课实现本来使用 CPU x264。

对应源码不以泛泛的主页链接代替，实际随包提供。FFmpeg、x264 原实现没有打补丁；精确提交与 SHA-256、编译步骤见 `FFMPEG-BUNDLED-BUILD.md`。不同工具链的重建不保证逐字节相同。本预览包未做 Authenticode 签名。

本地已通过六项合成检查：组件版本／配置，GDI 设备编译支持，两段 H.264／AAC 编码，缩放与浮点 PCM，concat／MP4／音视频时长，完整解码及无声视频。测试没有采集桌面、麦克风或系统声音。完整包的构建和验收结果另记，未运行项不算通过。

升级前停止录课并保存退出，备份个人配置和视频；解压到新目录，不覆盖运行中的文件。已发布版说明见 [InDev 20261002](https://github.com/tempChanghong/NPEduTools/releases/tag/InDev-20261002)，其单独安装 FFmpeg 的说明仍适用于该版附件。本文为下一版草稿。
