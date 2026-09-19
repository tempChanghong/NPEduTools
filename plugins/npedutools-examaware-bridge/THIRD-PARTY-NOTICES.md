# 第三方组件

本插件使用 ExamAware 官方 `@dsz-examaware/plugin-sdk` 1.5.2（ExamAware，GPL-3.0-only）。主进程和渲染进程 bundle 含所需 SDK 生命周期与错误处理代码。上游源码：

https://github.com/ExamAware/ExamAware2/tree/7979213fed918eaece7a5bf424e15f534778d7f2/packages/plugin-sdk

同一标签关联的 core 1.1.1、rpc 0.3.0 均为 GPL-3.0-only；安装版本及完整依赖信息记录在 `package-lock.json`。插件源码、构建配置与 GPL v3 全文随包提供；可使用锁定的依赖重新构建。
