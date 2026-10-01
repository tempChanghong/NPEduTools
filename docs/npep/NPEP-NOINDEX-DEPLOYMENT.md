# NPClassworks 前端 noindex 发布记录

日期：2026-09-22（北京时间）。用户明确要求推送 noindex 修改并立即部署，随后确认已恢复部署 Agent。

## 最新状态：已部署，线上核验通过

[生产运行 35626465181](https://github.com/tempChanghong/NPClassworks/actions/runs/35626465181) 全部成功，浏览器测试 152/152 通过。Agent 于北京时间 00:43:30 返回 DEPLOY_COMPLETED、exitCode=0，并确认固定版本与下列 main 一致。

00:45 实测首页、settings、classworks-admin、setup、classworks-2、404 和 index.html 共七个入口均为 HTTP 200，且各自只有一个 `noindex, follow` robots meta。API /ready 为 200；NPEP info 为预期的 503/TEMPORARILY_UNAVAILABLE，仍保持关闭。Agent 为 ok=true、busy=false、queued=0。

本次自动升级前备份：`/NPClassworksKV/deploy/backups/npclassworks_NPClassworksKV_20260921T164238Z_pre-upgrade_IVFWzJoKDM.dump`。未执行回滚或数据库恢复。

- 前端 main：`10b0f9a428ccbd00dcd33da56232f1a21c4e5c3b`。
- 后端 main 保持：`401182fa97866820828df37a9a1b52884056964b`。
- 前端 production-deploy 已恢复；后端 production-deploy 与 docker-publish 继续暂停。本轮未启用 NPEP，也未修改业务设置。

## 代码与覆盖范围

提交 `e84a0f7064dafc518ccbd1f134bd61b13c5c81bb` 仅修改 `index.html` 一行：`index, follow` → `noindex, follow`。随后测试夹具修复提交 10b0f9a 没有修改产品代码或此标签。已按授权将 feature 推送并以 force=false 快进 main。

已核实源入口与生产 `dist/index.html` 均只有一个 robots meta，值为 `noindex, follow`。生产 build、PWA validate 通过。各 Vue 功能路由共用此 HTML；Nginx history fallback 和 PWA navigation fallback 均指向 index.html。public 无独立 HTML，dist 只有 index.html，src 未发现运行时 robots 标签覆盖。设计原型 docs/prototypes/screen-layout.html 不在生产 dist 中。

此覆盖指 NPClassworks 前端功能页面，不包括其他独立站点、后端或代理错误页及非 HTML 资源。已有旧版离线缓存须联网更新后才采用新入口。

就排除搜索结果而言，noindex 已是完整指令，并无更强等级。nofollow 控制链接跟踪，nosnippet 控制摘要；Google 当前忽略 noarchive。没有把“是否最强”的提问当成改为 nofollow 的指令。没有设置会阻止搜索引擎读取 noindex 的全站 robots.txt Disallow。该标签不能强迫所有爬虫遵守，也不保证历史收录立即移除。

参考：[Google robots meta 规则](https://developers.google.com/search/docs/crawling-indexing/robots-meta-tag)、[阻止索引](https://developers.google.com/search/docs/crawling-indexing/block-indexing)。

## 发布中遇到的检查失败及修复

首次 [运行 35624822125](https://github.com/tempChanghong/NPClassworks/actions/runs/35624822125) 的 verify 成功，但整体失败、deploy skipped，未改变生产服务：

- contracts 实际通过 37 通用测试（另 1 预期跳过）和强制 N1 1/1；最后上传产物 FinalizeArtifact 返回 HTTP 403，导致 job 失败。
- browser 为 147 pass / 3 fail。两个 homework-overview 用例及一个 screen-layout 用例混用了浏览器本地作业日期和固定上海时区的需带物品日期；运行在 UTC16点以后时两者跨日。错误截图显示浏览器日期仍为 9月21日，上海已是 9月22日。

修复提交 10b0f9a 只改两份 E2E spec：分离 boardDate 与 preparationDate，显式 UTC 浏览器和固定 15:30Z/16:30Z 时刻，保留原有断言。独立日期函数检查确认跨日原因；合作任务复现旧 overview 两项失败，修正版两份完整 spec 11/11 通过，ESLint 和 diff 检查通过。未弱化断言、调整超时/重试、改变产品逻辑或工作流。

确认旧运行结束且未部署后，临时暂停前端发布入口，快进 main 至 10b0f9a，再恢复入口并单次手动 dispatch。新一轮 152 项浏览器测试及其余原有门槛全部通过，未绕过任何检查。

## 证据

- [初始前置状态](NPEP-NOINDEX-PREFLIGHT.json)
- [初始 main 快进](NPEP-NOINDEX-MAIN-RESULT.json)
- [上线前六个入口响应](NPEP-NOINDEX-ONLINE-BEFORE.json)：均为 index, follow。
- [恢复 Agent 后首次派发](NPEP-NOINDEX-DISPATCH.json)
- [日期修复及第二次派发](NPEP-NOINDEX-DATE-FIX-DISPATCH.json)

- [工作流最终结果](NPEP-NOINDEX-RUN-RESULT.json)
- [Agent 实际部署摘要](NPEP-NOINDEX-AGENT-RESULT.json)
- [上线后逐入口核验](NPEP-NOINDEX-ONLINE-AFTER.json)
