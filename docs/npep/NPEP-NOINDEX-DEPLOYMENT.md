# NPClassworks 前端 noindex 发布记录

日期：2026-09-22（北京时间）。用户明确要求把另一个任务准备的 noindex 修改推送入库并立即部署，覆盖所有前端页面。

## 代码与覆盖范围

- 前端提交：`e84a0f7064dafc518ccbd1f134bd61b13c5c81bb`，仅 `index.html` 一行，从 `index, follow` 改为 `noindex, follow`。
- 已推送 `codex/npep-n1-admin-ui` 并按用户授权 `force=false` 快进前端 main。后端 main 保持 `401182fa97866820828df37a9a1b52884056964b`。
- 已核实源入口及生产 `dist/index.html` 均只有一个 robots meta，值为 `noindex, follow`。合作任务此前的生产 build、PWA validate 均通过，根任务复核实际产物和仅一行 diff，没有重复构建。
- `src/pages` 的各 Vue 路由共用此 HTML；Nginx history fallback 与 PWA navigation fallback 均指向 index.html。public 无额外 HTML，dist 只有 index.html，src 未发现运行时 robots metadata 覆盖。设计原型 docs/prototypes/screen-layout.html 不在生产 dist 中。
- 此覆盖指 NPClassworks 前端功能页面；不声称后端自带 HTML、代理错误页、独立站点或非 HTML 资源都带这个 meta。noindex 也不保证搜索引擎立即移除历史收录或所有爬虫遵守。

## 尚未部署：代理仍停止

本轮查询 `https://deploy.newfires.top/healthz` 返回 502，符合此前 PM2 `np-deploy-agent` 停止状态。根任务没有服务器终端连接，已请求河豚豚执行 `pm2 restart np-deploy-agent` 并回报结果；不是在等待新的发布许可。

三个 GitHub 发布工作流复核仍为 disabled_manually。尚未启用工作流或触发一个必然无法连接代理的生产任务。线上首页、settings、classworks-admin、setup、classworks-2、404 六个入口均仍是 `index, follow`，所以不能声称 noindex 已上线。

现场恢复代理后，先核对 HTTPS healthz 的 ok=true、busy=false、queued=0，再恢复本次需要的前端发布工作流、从已核实的 main 发起一次生产流水线。遵循现有验证门槛，不跳过 verify/browser/contracts。部署后确认线上各功能入口实际返回 noindex，同时验证 API 与 NPEP 关闭状态。其余后台发布入口的恢复仍与上轮维护收尾协调，不能重复触发旧发布。

证据：

- [前置状态](NPEP-NOINDEX-PREFLIGHT.json)
- [main 快进实际结果](NPEP-NOINDEX-MAIN-RESULT.json)
- [上线前各入口响应](NPEP-NOINDEX-ONLINE-BEFORE.json)
- [Google noindex 官方说明](https://developers.google.com/search/docs/crawling-indexing/block-indexing)
