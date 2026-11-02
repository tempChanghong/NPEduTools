# 自习座位表展示回执：有界世代与兼容协商

这项修复只影响“哪个展示端最近确认看到了座位表”的回执，不改变座位表上传、图片下载、定时自习时段、采集状态或临时收起时长。

## 本机 App → Host

- `StudySeatingState.ObservationEpoch` 与 `StudySeatingPulse.ObservationEpoch` 是可选字段。新版 App 使用只读能力 `study.status.seating-epoch` 取得它，同时保留原来的照片、互联及校准状态。
- 新版 Host 对所有未选择该能力的响应剥离嵌套的 `ObservationEpoch`，兼容旧 App 的严格 JSON 反序列化。新版 App 遇到旧 Host 的 `UnknownCapability` 时退回既有校准/照片状态能力，60 秒后才再次探测，不逐轮额外请求。
- 每一座位表观测世代最多记录 16 个 App。已有序号仍作防重放记录，不逐项 LRU 淘汰。只有全部观测都已过期、且新请求带着当前世代时，Host 才更换座位观测世代并拒绝本次旧世代请求。App 下一次读取状态后携新世代重试。
- 已淘汰世代和不带世代的迟到请求不能再次推进或刷新观测。一旦发生世代轮转，本 Host 生命周期内不会再恢复无世代路径；图片缓存过期、撤回、重新下载也不会解除围栏。
- 这是座位回执专用世代，不更换整个自习展示实例，不重播提示音、不重新分页、不重置临时收起。16 个同时新鲜的客户端仍明确拒绝超额。

## Host → KV（继续使用座位子协议 0.14）

1. 新 Host 在清单请求添加 `X-NPEP-Seating-Sequence: 1`，第一次请求正文保持旧格式。
2. 新 KV 只对带该头的请求在清单响应增加 `supportsHostSequence: true`；旧客户端得到的响应格式不变。旧 KV 忽略该头，返回旧清单，新 Host 继续旧格式。
3. 确认能力后，新 Host 在 `observation` 增加 `hostSequence`。这是 Host 对实际选中的新鲜 App 观测分配的单调序号，真实 `appInstanceId` 与 App 内的 `sequence` 保留。重复读取同一观测不会增加序号，也不会续接服务端新鲜度。
4. KV 在同一已认证 NPEP context 中只存一个 Host 序号，不再积累 App GUID；新序号才可更新观测。启用后，旧序号、重复序号及降级旧格式都不能刷新该 context 的观测。新 context 仍受既有会话/设备身份围栏约束。
5. 回滚至不支持该字段的旧 KV 时，Host 收到 `400 INVALID_REQUEST` 后，下次轮询重新协商旧格式，不立刻重发排队样本。更新前旧 KV 的防重放/实例上限行为保持其旧版本边界。

建议先更新 KV，再整体更新 NPEduTools（App 与 Host）。只更新其中一端可继续传图，但旧路径仍有旧的 16 实例生命周期限制。无数据库迁移。自动回归使用 mock KV、私有 Named Pipe 和合成图片，不代表真实大屏、生产数据库或网络验收。
