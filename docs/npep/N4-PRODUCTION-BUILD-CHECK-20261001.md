# N4 收尾：网页生产构建检查

2026-10-01 · **本地检查通过，已修复检查工具问题；未提交、推送或部署。** 不构建桌面 App／发行 ZIP，不使用 Docker、实际学校账号、生产库或真实麦克风。

| 任务卡 | 内容 |
| --- | --- |
| 目标 | 验证当前网页能生成生产 PWA，并核对发布检查是否有效 |
| 范围 | 网页构建、静态 PWA 校验、代码检查、网页测试、后端发布配置测试 |
| 验收 | 构建和检查退出成功；隔离输出不能污染源码检查，也不能掩盖真实源码错误 |
| 仍待做 | PWA 实际升级、GitHub CI、最终提交组合、真实排程及班级大屏验收 |

## 修复了什么

- ESLint 原先扫描 `.artifacts` 的隔离浏览器依赖及 `.cache` 的生成缓存，初次报出 3463 项问题。现在只排除这些生成目录；作者编写的脚本、组件和测试继续检查，没有关闭规则。回归确认源码中的未声明变量仍被拒绝。
- 学校设备管理组件的多属性格式及浏览器测试脚本的 `window.performance` 引用已修正，不改变产品行为。
- `validate-pwa-build.js` 保留默认检查 `dist`，新增 `--dist <目录>`，可以验证隔离输出。错误参数返回失败，不能悄悄改查旧包；缺失 worker、禁用状态下混入分析代码仍会失败。
- 网页生产部署流水线在构建后增加 `pnpm pwa:validate`。这只是本地修改，尚未在 GitHub 执行，没有触发部署代理。

## 实际结果

| 检查 | 本次结果 |
| --- | --- |
| 真实 Vite 生产构建与 Workbox 输出 | 通过，独立目录，不覆盖原有 `dist` |
| 静态 PWA 资源校验 | 通过：manifest、图标、worker、导航回退与禁止混入资源等 |
| 编译后 HTML | `robots` 含 `noindex`，6 个入口资源引用均存在；不据此宣称生产网站已更新 |
| 全网页 ESLint | 通过，0 错误／0 警告 |
| `pnpm test` | 654 通过，0 失败／0 跳过；包含新增 5 项工具回归 |
| 后端发布配置、失败诊断与版本配对 | 21 通过，0 失败／0 跳过；临时 Git 仓库、命令替身及 Git Bash 测试分支，不运行容器 |
| 最后修改的流水线检查及工具回归 | 11 通过；与 654 有重叠，不重复累加 |

本地 Node 为 `24.14.1`，托管流水线使用 Node 22；没有用本轮结果冒充 Node 22 或 GitHub 实际通过。Sass 旧 API 与浏览器兼容数据版本提示仍存在，不影响本次构建退出成功；没有为消除提示而升级依赖。

完整[检查清单](../../../NPClassworks/.artifacts/n4-release-check/20261001-223308-e22839d6/result.json)保留各阶段日志、三仓基线及未提交状态、关键产物和本次检查代码 SHA-256。原始失败日志保留用于解释修复，不覆盖最后通过结果。

产物为网页静态检查输出，不是已经发布的应用。清单始终 `releaseReady=false`，真实学校时间／麦克风、PWA 运行时升级、班级大屏、托管 CI、桌面发行包和部署均明确列为未执行。

## 以后如何检查

在 NPClassworks 根目录，已有默认构建方式不变：

```powershell
pnpm run lint:check
pnpm test
pnpm run build
pnpm pwa:validate
```

如需保留原 `dist`，先让 Vite 构建到独立输出目录，再显式检查该目录：

```powershell
pnpm exec vite build --outDir .artifacts/release-candidate/dist
node scripts/validate-pwa-build.js --dist .artifacts/release-candidate/dist
```

这些命令只检查本地代码／构建，不会推送或部署。跨端功能继续使用既有 [N4 统一验收入口](N4-UNIFIED-ACCEPTANCE-20261001.md)，不把静态 PWA 校验称作浏览器实际升级验收。
