# NPEP N2 后端生产部署记录

> 归档记录：正文保留当时的范围、决定与验证结果，不代表当前功能、发布或部署状态。[历史资料索引](../README.md)。

2026-09-24，用户明确授权“先把后端推送部署”。北京时间 23:15:30，部署代理返回 `DEPLOY_COMPLETED`、exitCode=0。

- 后端：`ebcdc74c4cd862a3fbca39b23225fdfd88e89576`，已推送 NPClassworksKV 的 main 及 codex/npep-n2-notifications-release。
- 前端：继续使用已发布 main `8cc9dd4a820877b28586d2a9acc1c614f1007e29`；本次未提交、推送前端 N2 改动。既有 shared 部署脚本会重建/重建容器，前端源码版本没有变化。
- 恢复启用后端 Deploy production server 工作流；Docker Build and Push 保持原停用状态。
- [生产工作流](https://github.com/tempChanghong/NPClassworksKV/actions/runs/36017770035)：verify、fullstack、deploy 全部成功。
- [独立 Quality 工作流](https://github.com/tempChanghong/NPClassworksKV/actions/runs/36017770026)：成功。

生产代理执行现有升级脚本，先备份，后固定双仓版本、构建及健康检查。启动命令先执行 Prisma migrate deploy，再启动应用；本次包含 20260924000000_npep_n2_notifications 的三个新增表。

升级前备份：`/NPClassworksKV/deploy/backups/npclassworks_NPClassworksKV_20260924T151142Z_pre-upgrade_CVdI8SCTFQ.dump`。部署日志确认备份完成；本轮未额外对这份生产备份做恢复演练，也未核实其异地上传状态。

## 线上验证

- `https://api.newfires.top/ready`：HTTP 200。
- N1 `/api/v2/npep/info` 使用 0.1 头：HTTP 200，服务实例和 deploymentEpoch 与升级前完全相同，能力仍为 device.status；未重新初始化配对。
- N2 `/api/v2/npep/device/notifications` 使用 0.2 头、不带凭据：HTTP 401 / AUTH_INVALID，响应协议为 0.2，确认新路由已上线且没有开放匿名访问。
- `https://newfires.top/`：HTTP 200。
- 部署代理：ok=true、busy=false、queued=0。

本轮没有向实际班级发送测试通知，也没有使用生产设备凭据拉取正文；教师发布至实体大屏弹窗仍需单设备现场验收。N2 桌面试用包可用于该验收，网页新增回执界面仍待单独发布。

本地部署日志：`D:/WebstormProjects/NPClassworksKV/deploy/runtime/npep-n2-production-deploy.log`（ignored）。后端工作区在推送后保持干净。
