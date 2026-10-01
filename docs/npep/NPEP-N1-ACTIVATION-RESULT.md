# NPEP N1 首次生产激活结果

日期：2026-09-24（北京时间）。

河豚豚已确认网站原有功能一切正常，原有业务人工验收通过。启用前现场检查：卷 npclassworks_npep-config 挂载至 /var/lib/npclassworks-npep，可写；容器 NPEP_ENABLED=false；deployment.json 不存在。

现场执行首次初始化、调整生产开关、重建后端及激活时 SSH 意外断开，没有保留完整输出。重新执行第三步时，首次激活检查发现历史 NPEP 登记并停止。该提示不能单独证明失败或成功。

协调任务随后于北京时间 18:17 直接进行 HTTPS 只读核验：

- GET https://api.newfires.top/api/v2/npep/info：HTTP 200，protocolVersion=0.1。
- serverInstanceId：e51c36e4-ff7c-4b3c-bf79-e21205da59ad。
- deploymentEpoch：964bb3c3-4105-4080-ab28-81939b97d161。
- supportedCapabilities：仅 device.status。
- GET https://api.newfires.top/ready：HTTP 200，status=success。

核对实现：info 经 npepService 的 transaction 调用 assertDeployment，既要求外部配置启用，也要求数据库中的实例与代际一致。因此此次 200 确认当前 NPEP 激活已完成且两处身份一致，并非只有进程存活。无法复原 SSH 断开时每条命令的输出，但无需再次激活。

下一步：NPEduTools → 设置 → 学校互联，服务地址 https://api.newfires.top；设备创建短码、管理员在网页批准、设备本机确认，再核验上报、断线重连和撤销。尚无生产设备配对或大屏现场验收完成证据。

不要重新 init、删除身份文件、轮换代际或重复执行首次激活脚本。已有历史发布文档中“尚未启用”的状态仅代表当时情况，以本记录为准。
