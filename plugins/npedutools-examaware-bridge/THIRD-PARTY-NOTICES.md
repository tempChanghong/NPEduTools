# 第三方组件

本插件使用 ExamAware 官方 `@dsz-examaware/plugin-sdk` 1.5.2（ExamAware，GPL-3.0-only）。主进程和渲染进程 bundle 含所需 SDK 生命周期与错误处理代码。上游源码：

https://github.com/ExamAware/ExamAware2/tree/7979213fed918eaece7a5bf424e15f534778d7f2/packages/plugin-sdk

关联的 core 1.1.1、rpc 0.3.0 均为 GPL-3.0-only，其 npm gitHead 分别为 `d27cfebf7f5becbd4b08ba30b4241441328dc91a`、`ee1dee1b9e4912099d506a16547f35604e736d23`，并非同一发布提交。安装版本及完整依赖信息记录在 `package-lock.json`。

插件源码、构建配置、上游 GPL v3 全文（UPSTREAM-LICENSE.txt）与本项目 LICENSE 随包提供。`dist/bundle-inputs.json` 列出实际打入的第三方输入。完整上游 TypeScript 源码及构建文件见 [InDev 20260920 发布附件](https://github.com/tempChanghong/NPEduTools/releases/tag/InDev-20260920) 中的 `NPEduTools-InDev-20260920-third-party-sources.zip`；完整程序包内 `third-party/sources/` 也包含相同归档。

解开 plugin-sdk 对应归档，使用根 package.json 指定的 pnpm 运行 `pnpm install --frozen-lockfile`、`pnpm --filter @dsz-examaware/plugin-sdk build`；本插件使用 `npm ci --ignore-scripts`、`npm run build` 重建。完整构建、替换说明随程序包的 THIRD-PARTY-MATERIALS.md 提供。
