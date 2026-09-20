# InDev 20260920 发版审核记录

日期：2026-09-20。与 [发版说明](InDev-20260920.md) 配套。本记录更新为用户选择“程序包不附带 FFmpeg，增加单独安装步骤”后的候选；此前带 FFmpeg 的附件已废弃，不用于公开发布。

## 发布状态与基线

- 名称 **InDev 20260920**，拟用标签 `InDev-20260920`。
- [GitHub Release 草稿](https://github.com/tempChanghong/NPEduTools/releases/tag/untagged-317fc8d5aa40259361a9)：继续保持 draft、prerelease，不设为 Latest，未公开发布。
- 原 PR #1 已由维护者合并。本轮修订分支 `codex/release-third-party-materials` 基于主分支合并提交 `3d729bb`，需单独审核并合并。
- 新候选源码基线：`0a04ca9ee4d0c348d106b987b7c3d8783ec2c394`。C 盘新克隆、`workingTreeDirty=false`，未带入开发用 `.tools/recording`。
- SDK 10.0.400；Windows x64 自包含、不裁剪、非单文件，.NET 运行时 10.0.11。
- 主程序 ProductVersion：`InDev 20260920+0a04ca9ee4d0c348d106b987b7c3d8783ec2c394`。本审核记录在打包后更新，不改变二进制基线。

## 本轮修改

1. 打包过程不再引导安装或复制 FFmpeg；`recordingToolsBundled=false`。发布检查拒绝包内任何 FFmpeg/ffprobe EXE。
2. 提供兼容 Windows PowerShell 5.1 的 `Install-Recording-Tools.ps1`，从 Gyan 的固定 9.0.1 发布直接下载；写入前验证 ZIP 和两个 EXE 的 SHA-256。支持离线 ZIP，不提权、不更改 PATH 或执行策略。
3. 安装说明包含手动放置方式、两处录制器目录、更新时重新安装和安装后校验。录制器缺少组件时提示该说明；程序启动不会自动下载。
4. 根据实际发布的 21 个 NuGet 包补齐许可原文，附运行时声明；固定 11 份补充许可/声明的上游地址及哈希。
5. 随包提供 ClassIsland 契约库及 ExamAware SDK 关联版本的四份完整仓库源码归档，含构建文件、锁文件及 SHA-256。桥接插件记录 esbuild 输入并附上游 GPL 全文，纠正 SDK/core/rpc 同一提交的旧描述。

范围和构建/替换说明见 [第三方材料](../THIRD-PARTY-MATERIALS.md)。本次未将上游 NuGet/npm 包逐字节重建验证，也不声称历史候选包均已完成这些修订。

## 附件

| 文件 | 字节数 | 用途 |
| --- | ---: | --- |
| `NPEduTools-InDev-20260920-win-x64.zip` | 310414959 | 程序、运行时、两个桥接、安装说明、本项目及第三方源码；不含 FFmpeg |
| `NPEduTools-InDev-20260920-source.zip` | 2110604 | 包内同一份项目源码快照 |
| `NPEduTools-InDev-20260920-third-party-sources.zip` | 52166780 | 包内同一组上游对应源码，供独立下载 |
| `NPEduTools.ClassIsland.Bridge.cipx` | 62716 | ClassIsland 桥接 0.2.0.0 |
| `npedutools-examaware-bridge-0.3.0.ea2x` | 62717 | ExamAware2 桥接 0.3.0，补充上游许可与构建输入清单 |
| `InDev-20260920.md` | 7641 | 发版说明 |
| `SHA256SUMS.txt` | 619 | 以上六个附件的 SHA-256 |

Windows ZIP 约 296.0 MiB，SHA-256：

```text
b6410232c09c955643813cd6d470f8277461ab9ce0809ec5dc3ddf7a9bdc37e6
```

独立第三方源码 ZIP SHA-256：

```text
6f173b2b6a1ed0dbdf6c2b8e1b33787aac6fd91cd719db2931cc1ecdc2c20c23
```

本地附件在 `.artifacts/releases/InDev-20260920/`，与 C 盘构建副本逐文件核对。GitHub 上传验证文件长度及服务端 SHA-256。项目源码对应其二进制基线；GitHub 自动生成的 Source code 附件不能代替这里提供的第三方材料。

## 本轮验证

| 检查 | 结果 |
| --- | --- |
| 新克隆工作区、锁定还原、自包含发布 | 通过，工作区干净 |
| .NET 回归 | 350/350 通过，0 失败、0 跳过 |
| ExamAware TypeScript 构建和集成回归 | 19/19 通过 |
| 包清单及新目录解压检查 | 1558 个文件通过；两个桥接、五个组件运行时齐全 |
| 发布目录及项目源码归档 | 无 FFmpeg/ffprobe EXE 或 FFmpeg ZIP；源码含安装脚本、版本哈希配置及材料锁文件 |
| 许可与源码锁定信息 | 11 份许可/声明、4 份源码归档哈希通过；检查契约项目、SDK TypeScript 源码与工作区构建输入 |
| 未安装组件 | Recorder --probe 返回 ready=false，并提示 RECORDING-TOOLS-INSTALL.md |
| Windows PowerShell 5.1 联网安装 | 直接从上游下载，ZIP/EXE 哈希通过，两处目录安装成功 |
| Windows PowerShell 5.1 离线及重复安装 | 实际解压包中通过；安装后 Recorder --probe 返回 ready=true |
| 损坏 ZIP | 校验失败，在安装目录写入之前退出 |
| 安装后完整性 | 显式 AllowInstalledRecordingTools 模式通过；替换一处 ffprobe 内容后拒绝，恢复后通过 |
| 防止误分发 | 默认发布校验拒绝已经安装 FFmpeg 的目录 |
| 安装后媒体处理 | 2 秒 720p/8 fps 合成 H.264/AAC 视频编码、探测及解码通过；未采集屏幕或麦克风 |

安装试验使用另一份解压目录，上传的 ZIP 保持未安装 FFmpeg。日志保存在 `.artifacts/release-indev-20260920/`，不对外上传用户环境日志。

本轮没有重新执行 GUI、UAC、真实课堂模式切换、屏幕/音频录制或长课验证；这些结果不可由合成媒体检查代替。旧候选的 GUI 和模式验收仅作为历史记录，见 [模式异常验收](../CLASSROOM-EXCEPTION-ACCEPTANCE.md) 和 [P4 验证](../PORTABLE-RELEASE-VALIDATION.md)。

## 保留的限制与公开前步骤

- ExamAware2 1.5.2 编辑器退出问题按维护者决定暂不修复。切回日常前应保存关闭编辑器并结束放映，不把该缺陷写成已通过。
- 自动录制重启后关闭；二分钟铃按课表前两分钟推算，无有效学校时间时禁止开始新的自动任务。
- 目标大屏整课、连续多节、实际双音源、休眠、触摸、多屏 DPI 和注销登录等仍待现场验证。
- 本包未做 Authenticode 签名，哈希校验不替代数字签名。

- [x] 移除随包 FFmpeg 并验证独立安装路径。
- [x] 为当前实际发布的组件补充许可证、对应源码及构建说明。
- [ ] 维护者审核此次修订 PR、发版说明与七个附件，再合并修订。
- [ ] 审核后才公开 Release。标签应指向上述经过验证的源码基线；若改用其他合并提交或修改附件，应同步检查源码映射与哈希，不能仅移动标签而忽略包内基线。

原先“缺少随包 FFmpeg 完整对应源码”的阻碍通过取消该二进制再分发解决；开发预览标记仍不替代材料核对。Release 保持草稿供维护者最终审核。
