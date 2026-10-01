# NPClassworks 前端站点元数据修正

日期：2026-09-22。河豚豚指出线上 HTML head 遗留 cs.newfires.top，要求同步修改并保证语义性和可维护性。

## 修改

前端提交：`8cc9dd4a820877b28586d2a9acc1c614f1007e29`。已推送 feature 和 main；后端保持 `401182fa97866820828df37a9a1b52884056964b`。

- 删除 index.html 中四处硬编码的旧域名。
- Vite 的 site-metadata 插件在构建及开发服务中统一生成 canonical、og:url、og:image、twitter:image，默认前端 origin 为 https://newfires.top。
- VITE_SITE_ORIGIN 单独表示前端站点地址，不复用 VITE_DEFAULT_KV_SERVER。通过 URL 解析，拒绝非 HTTP(S)、账号密码、路径、查询参数和 fragment。
- 分享图片路径保持一致，由站点地址统一解析为绝对 URL。
- Docker 暴露同名构建参数，README 说明普通构建、Docker/Compose 用法及重新构建要求。
- robots 继续 noindex, follow。历史部署文档和后端示例域名未做全局替换。

## 本地验证

- ESLint（vite.config.mjs）和 git diff --check 通过。
- 生产构建及 PWA 校验通过；实际 dist/index.html 四项地址均为 newfires.top，未包含 cs.newfires.top，noindex 保持。
- 实际加载 Vite 配置并检查元数据 hook：自定义带端口 HTTPS origin 正确应用至四项；FTP、凭据、非根路径、查询和 fragment 五种非法配置均拒绝。

## 发布

推送前确认 Agent 空闲、前后端 main 匹配预期、前端工作流 active 且无在途发布。普通 fast-forward push 自动触发一次 [生产工作流 35686135000](https://github.com/tempChanghong/NPClassworks/actions/runs/35686135000)，未额外手动 dispatch。遵循原有 verify/browser/contracts 门槛。

已完成：verify、contracts、browser、deploy 全部成功，浏览器测试 152/152。Agent 返回 DEPLOY_COMPLETED，固定前端 8cc9dd4、后端 401182f。上线后七个入口均为 HTTP 200、四项站点元数据正确且没有旧域名，robots 保持 noindex, follow；分享图片 HTTP 200 / image/png，API ready 为 200，NPEP 仍 503/TEMPORARILY_UNAVAILABLE，Agent 空闲。详见 [线上核验记录](NPEP-SITE-METADATA-ONLINE-AFTER.json)。
