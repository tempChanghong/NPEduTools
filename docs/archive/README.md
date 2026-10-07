# 历史资料

[返回当前文档](../README.md)

这里保留研究、被后续方案替代的设计、阶段验收和旧交付记录。归档不撤销当时的结果，也不把旧失败改记为通过；它只将这些资料从当前操作入口中分离。

| 分类 | 内容 |
| --- | --- |
| [开发与原型](development/README.md) | 原架构规划、M0／M1、旧本地环境 |
| [课堂与桥接](classroom/README.md) | ClassIsland／ExamAware2 联调、自启动、模式往返和异常验收 |
| [录课](recording/README.md) | 自动录课 P0～P3、学校时钟、计划、执行及旧便携包检查 |
| [学校互联](npep/README.md) | N1～N4、通知、考试、噪音、首次部署与恢复记录 |
| [交付](releases/README.md) | 旧预览包、审核、合并与发布草稿；已发布版本另见[版本索引](../releases/README.md) |
| [外部软件研究](research/README.md) | Datedu／C30、软件接入研究和最初的产品构想 |
| [界面历史](ui/README.md) | 旧主页、侧边栏与品牌接入 |
| [旧 README 快照](README-DEVELOPMENT-HISTORY-20261003.md) | 2026-10-03 前混合在仓库首页的开发与验证记录 |

历史正文只调整链接和归档提示。命令、原机路径、受测提交和状态快照按当时含义保留；不要直接执行旧上线或激活命令。原链接迁移后请从本索引查找文件名；旧版本的文件仍可从其 Git 标签查看。查看当前功能请回到[使用指南](../GETTING-STARTED.md)，开发和检查从[开发指南](../DEVELOPMENT.md)进入。

## 外部参考资料

以下目录是仓库固定的 Git 子模块，不参与本轮归档，也不是 NPEduTools 的当前产品文档：

- [ClassIsland 开发资料](../classisland-docs-next/README.md)
- [ExamAware2 资料](../ExamAware-docs/README.md)
- [PowerPoint Touch Assist](../PowerPoint-Touch-Assist/README.md)

干净工作树可能尚未初始化这些目录。需要阅读时，从仓库根目录执行：

```powershell
git submodule update --init -- docs/classisland-docs-next docs/ExamAware-docs docs/PowerPoint-Touch-Assist
```

核查源码时以对应上游版本为准。文档链接检查会明确排除这些上游目录，不将它们计作本仓库链接已验证。
