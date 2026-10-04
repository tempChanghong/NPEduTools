# Windows 安装包 · 2026-10-03

**本轮交付：为下一版增加 Inno Setup 安装入口，保留 ZIP。** 公开版本号待确定；不修改已有 Release。

| 用户操作 | 结果 |
| --- | --- |
| 首次安装 | 固定程序目录、开始菜单入口、可选桌面快捷方式，随后进入原 OOBE |
| 运行中升级或卸载 | 阻止继续，先保存并完整退出；不强杀进程 |
| 覆盖升级 | 校验版本，保留账户配置、配对与视频 |
| 卸载 | 移除登记文件，保留用户数据；提示先关闭本应用登录启动 |
| 从 ZIP 迁移 | 关闭旧位置登录启动，再安装并从新入口重新登记 |

入口：[使用与构建说明](../INSTALLER.md) · [安装脚本](../../installer/NPEduTools.iss) · [统一打包脚本](../../scripts/package-npedutools.ps1)。

## 验证状态

- 本地：生产 `.iss` 实际编译通过；8 项打包检查与 21 项隔离安装检查通过。PowerShell 7 额外采用超过 260 字符的许可路径做编译回归。
- Windows PowerShell 5.1／PowerShell 7 均通过。Inno 7 本轮记录：安装检查 `installer-tests/cb10d3bc30e74ef7a943cd8b2abb4deb`（5.1）与 `installer-tests/f04fc6ffd23a456d939545cf9115e377`（7）；打包检查 `installer-package-tests/d2c72e788f0a4f70bbbaf102a7e4372e`（5.1）与 `installer-package-tests/f62fb3ef423a49b9baacdc74d8fe8a36`（7），均位于 `.artifacts/`。
- 真实应用载荷：已通过统一入口完成自包含发布、两种桥接插件打包及全部 1577 个清单文件校验，生成本地 `Installer-preview-20261003` EXE 与 ZIP（`.artifacts/installer-preview-20261003/final`）。该名称仅为本地审核候选，不是下次公开版本号。
- CI：已增加独立入口，尚未推送运行。
- 真实安装、UAC／OOBE／短录及目标大屏人工验收：尚未进行。不把测试载荷 EXE 当作可用应用发放；需要试装时使用上述完整候选包。

限制：首版不提供自动更新，不自动清理旧版本遗留文件；运行状态检查不是原子维护锁，安装期间不要重开应用。多账户的未加载启动登记不能保证扫描到。当前无 Authenticode 签名。
