# 单独安装录制组件

InDev 20260920 的公开候选包不附带 `ffmpeg.exe`、`ffprobe.exe`。快捷启动、课堂模式和 PPT 触摸辅助可先使用；手动／自动录课需要先完成本步骤。NPEduTools 不会在后台自动下载组件。

## 方式一：安装脚本

1. 完整解压程序包到可写目录，关闭 NPEduTools。联网电脑打开该目录，在 Windows PowerShell 中执行：

   ```powershell
   powershell.exe -NoProfile -File .\Install-Recording-Tools.ps1
   ```

2. 脚本直接从 [Gyan 官方 GitHub 9.0.1 发布页](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.1)下载 `ffmpeg-9.0.1-essentials_build.zip`。它核对固定 ZIP 及两个 EXE 的 SHA-256，再复制文件到本包的两处录制器目录；不修改系统 PATH、不请求管理员权限、不改变脚本执行策略。
3. 重新打开 NPEduTools，在“录制微课”刷新设备，选择音源和目录，短录并回放后再启用自动录课。

如果学校策略禁止运行脚本，请使用下方手动步骤，无需调整安全策略。网络不可用时，可在另一台联网电脑从同一发布页下载 ZIP，带到大屏上后运行：

```powershell
powershell.exe -NoProfile -File .\Install-Recording-Tools.ps1 -ArchivePath "D:\下载\ffmpeg-9.0.1-essentials_build.zip"
```

## 方式二：手动放置

从上述上游发布页下载同名 ZIP，先核对完整压缩包的 SHA-256：

```powershell
Get-FileHash "D:\下载\ffmpeg-9.0.1-essentials_build.zip" -Algorithm SHA256
```

应为 `FEC81AE03971D9DD4BE3EBE02E263BD2EC1D789483F931BDBA5F5715E65DA2E9`。请勿使用会随时间变化的“latest”下载链接替代这个版本。

解压后，将 `bin/ffmpeg.exe`、`bin/ffprobe.exe`，以及压缩包根目录的 `LICENSE`、`README.txt`，分别复制到程序包内的 **两个**目录（不存在则新建）：

- `app/Recorder/Tools/`
- `app/Host/Recorder/Tools/`

每个 Tools 目录直接包含这四个文件，不要再多套一层 `bin/`。精确 EXE 校验值见包内 `recording-tools.json`。FFmpeg 及相关组件的许可证和说明由上游压缩包提供，请一并保留。

## 校验、更新与分发

包内校验脚本默认检查原始程序包没有捆绑媒体 EXE；完成用户安装后，使用 PowerShell 7 执行 `./verify-portable-package.ps1 -AllowInstalledRecordingTools`，会额外校验两处 FFmpeg 文件。

如果安装中断或提示哈希不符，不要开始录制；修复网络／存储问题后重新运行安装步骤。升级到新程序目录后，也要在新目录重新安装或从同一经校验的上游 ZIP 导入。

本步骤是最终用户直接取得上游组件，不代表 NPEduTools 随包再分发 FFmpeg。请分发原始程序 ZIP；不要将已安装 FFmpeg 的目录重新打包上传而忽略其对应分发要求。
