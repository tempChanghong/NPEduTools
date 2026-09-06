# 本地联调环境

核对日期：2026-09-06。本文记录用户已准备的本地环境，以及当前终端实际观察到的状态。

## 项目与资料

| 位置 | 用途 |
| --- | --- |
| `D:\WebstormProjects\NPEduTools` | WPF App、Host、CLI、Adapter 与测试 |
| `D:\WebstormProjects\ClassIsland` | ClassIsland 本体与插件开发源码、后续真实联调目标 |
| `docs/classisland-docs-next` | 本地开发文档，优先查阅对应章节，再与当前源码核对 |

ClassIsland 当前标签为 `2.1.0.1`，HEAD 为 `15273f82c9d2d55929df83b5fb806e68ee4547c0`，与 NPEduTools 固定的 ClassIsland.Shared.IPC 包标注提交一致；本次检查其工作区无改动。文档仓库 HEAD 为 `6b254076437bf92107ce9dabefc9b326ef2f0bb2`。

## 文档阅读入口

- [配置本体开发环境](classisland-docs-next/src/dev/get-started/development.md)
- [配置插件开发环境](classisland-docs-next/src/dev/get-started/development-plugins.md)
- [开始编写插件](classisland-docs-next/src/dev/plugins/create-project.md)
- [插件依赖](classisland-docs-next/src/dev/plugins/dependency.md)
- [跨进程通信](classisland-docs-next/src/dev/ipc/README.md)
- [使用 IPC](classisland-docs-next/src/dev/ipc/ipc.md)
- [IPC 服务与事件参考](classisland-docs-next/src/dev/ipc/reference.md)

文档说明了 .NET 8 桌面/插件开发、PowerShell Core、子模块与插件调试流程。具体 SDK 选择、启动项目和构建输出路径仍需以当前源码配置为准；不能仅按文档的示例路径判断构建已完成。

## 系统 SDK

`C:\Program Files\dotnet\sdk` 已存在：

- `8.0.424`
- `9.0.317`
- `10.0.400`

NPEduTools 的 `global.json` 选择 `10.0.400`。包装脚本在 NPEduTools 根目录验证 SDK 解析结果，优先系统 SDK、回退到已有项目内 SDK，测试子进程沿用同一个主机。

先前下载的 `.tools/dotnet` 保留作为备用，不需要再安装 SDK，也不需要修改系统环境变量。初次缺少 SDK 的记录属于历史情况，不代表当前环境。

通过包装脚本确认实际使用 `C:\Program Files\dotnet\sdk\10.0.400`，M1 Release 构建 0 警告、0 错误，27 项测试全部通过。桌面窗口使用 .NET 10 Desktop Runtime，启动与窗口验收见 [M1 记录](M1-READONLY-VALIDATION.md)。

## ClassIsland 的 SDK 解析（已解决）

当前 ClassIsland 的 `global.json` 内容为：

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": true
  }
}
```

此前仅安装 SDK 8 和 10 时，该目录返回“未找到兼容 SDK”。用户安装 `9.0.317` 后，已核实在该目录执行 `dotnet --version` 返回 `9.0.317`，无需更改仓库的 `global.json`。

已完成 `ClassIsland.Desktop` 的 Debug 构建及真实 IPC 联调。首次构建与用户运行的清理/构建脚本时间重叠，出现引用程序集缺失；随后串行构建成功，0 错误、1 条程序集版本冲突警告。本次未修改 ClassIsland 源码或 SDK 配置。

## 插件开发输出与联调状态

源码中的 `tools/plugin/build.ps1` 会清理、构建并写入用户级环境变量。其输出目标为：

```text
ClassIsland.Desktop/bin/Debug/net8.0-windows10.0.19041.0/ClassIsland.Desktop.exe
```

示例插件调试配置使用：

- `ClassIsland_DebugBinaryFile` 作为启动程序。
- `ClassIsland_DebugBinaryDirectory` 作为工作目录。
- `-epp $(TargetDir)` 指定外部插件目录。

当前已确认上述 Debug 可执行文件存在，产品版本为 `2.1.0.1+15273f82c9d2d55929df83b5fb806e68ee4547c0`。用户已启动官方插件开发初始化脚本；本轮工具读取用户级环境变量时仍未取得两个变量，可能尚未完成脚本末尾的设置步骤。NPEduTools 的外部 IPC 联调使用明确的可执行文件路径，不依赖这两个插件调试变量。

真实联调已通过，包含课程 A → 课间 → 课程 B 的自然状态/事件变化，以及本体退出、重启后的查询恢复，详见 [真实 ClassIsland 联调记录](CLASSISLAND-LIVE-VALIDATION.md)。当前公开课程服务足以支持这些操作；只有明确缺少所需控制接口时，再评估桥接插件。
