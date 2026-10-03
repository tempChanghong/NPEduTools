# 随包录制组件：维护者构建说明

当前尝试使用自行构建的 FFmpeg 9.0.1 与 x264 0.165.r3222，取代再分发完整 Gyan essentials。固定源代码提交、归档长度和 SHA-256 在 `recording-sources.lock.json` 中；x264 归档来自保留同一上游提交的 ShiftMediaProject 镜像。原始实现未打补丁。

## 构建

在 Windows 安装 MSYS2，使用 UCRT64 环境，先完成系统更新，再安装：

```bash
pacman --noconfirm -Syu
# 若更新关闭了终端，重新打开后继续更新与安装。
pacman --noconfirm -Syu --needed make mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf
```

PowerShell 中从 NPEduTools 项目根目录执行：

```powershell
./scripts/build-recording-tools.ps1 -MsysRoot C:/msys64 -Jobs 8
./scripts/test-recording-bundle.ps1
# 将 NEXT-VERSION 替换为尚未发布的版本号。
./scripts/package-npedutools.ps1 -ReleaseVersion NEXT-VERSION -ReleaseNotesPath docs/releases/NEXT-FFMPEG.md
```

组件脚本下载并核对两份精确源码归档，在独立构建目录编译静态 x264 和 FFmpeg，输出 `.tools/recording-bundled/`。工具链在 `configuration/toolchain-packages.txt` 中列出实际版本；MSYS2 安装包只是构建环境，不随桌面应用分发。首轮使用 MSYS2 2026-09-27 UCRT64，完整工具链版本随源码归档保留。

`build-recording-tools.sh` 包含全部配置命令。源码材料中的 `sources/` 解压到构建目录 `src/`；通过 `bash build-recording-tools.sh <构建目录> 8` 可以重新编译。生成配置、编译器／汇编器版本和 Windows PE 导入列表也保留在源码归档中。上游默认生成的版本字符串可能省略 Git 修订，精确身份以源码锁为准。不同工具链的重建不保证逐字节相同。

## 功能与许可

保留 Windows GDI 屏幕采集、浮点 PCM 输入、x264 H.264、原生 AAC、缩放、MKV 分段、concat 合并、MP4、ffprobe 及成片解码校验；运行时 CPU 指令分派保持启用，没有针对开发电脑使用 `-march=native`。没有网络、其他第三方编码库、nonfree 组件或 GPU 编码；现有录制实现使用 CPU x264。

FFmpeg 配置启用 GPL 与 version3，组合构建按 GPL-3.0-or-later 保留声明；x264 的原始 COPYING 和源码一并提供，GCC 运行时例外声明也保留。Windows 自带系统库不复制到程序包内。

发行包的两处 Recorder/Tools 都包含 ffmpeg.exe、ffprobe.exe、LICENSE、README.txt 和哈希清单；`third-party/ffmpeg/NPEduTools-recording-tools-source.zip` 携带原始源码归档、全部构建脚本、许可证与配置材料。分发二进制时应同时提供这份材料，不能只链接到可变的上游分支。

历史发布包原先单独安装 FFmpeg 的说明仍作为历史记录保留。尚未更新的旧 ZIP 不会因为源码脚本修改而自动获得组件。构建验证不代替真实教室大屏长课验收。

依据：[FFmpeg 许可说明](https://ffmpeg.org/legal.html)、[FFmpeg 官方源码仓库](https://github.com/FFmpeg/FFmpeg)、[x264 上游](https://www.videolan.org/developers/x264.html)、[MSYS2 构建环境](https://www.msys2.org/docs/ci/)。
