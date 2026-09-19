# NPEduTools 图标接入

日期：2026-09-19。以用户提供的 [branding 资产](../images/branding/README.md) 为源，已接入主应用。原始 PNG、AI 母版、SVG 和导出图像均未修改。

## 显示位置

| 位置 | 资源 |
| --- | --- |
| 应用 EXE、任务栏、窗口图标 | `images/branding/npedutools.ico`，保留其多尺寸帧 |
| 主页面左上角 | 小尺寸彩色矢量版，48 DIP |
| 侧边栏绿色展开按钮 | 白色单色矢量版，34 DIP；保留底部拖动提示线 |
| 主页面中的侧边栏示意 | 白色单色矢量版，26 DIP |
| Windows 通知区域 | 同一彩色 ICO，按系统小图标尺寸选择；暂未加入深浅主题自动切换 |
| 录制、预演、快捷启动编辑窗口 | 统一使用应用 ICO |

绿色图标底使用主题色 `#147D68`。应用内原先直接显示字体字母 n 的三处标识已替换；拖动入口文案同步改为“标志／图标”。

## 资源方式

WPF 不直接显示 SVG。通过 [sync-branding.ps1](../scripts/sync-branding.ps1) 将已批准 SVG 中的路径与填充色转换为 WPF `DrawingImage`，输出 [Branding.xaml](../src/NPEduTools.App/Resources/Branding.xaml)。保留源 SVG 的方形视口、透明边距、负空间及非零填充规则，不引入 SVG 渲染依赖，也不将矢量栅格化。

提供 `BrandLogo`、`BrandLogoSmall`、`BrandLogoLight` 三个资源。当前小尺寸显示使用简化版和单色版；标准版可供后续较大的品牌展示使用。

ICO 同时作为 EXE 的 `ApplicationIcon` 和 WPF 内嵌资源。运行时不依赖工作目录或外部 `images` 文件夹，也不需要携带 AI 母版、SVG 源文件。托盘图标从内嵌资源加载，克隆后独立持有，退出时释放。

更新资产后执行：

```powershell
./scripts/sync-branding.ps1
dotnet build src/NPEduTools.App/NPEduTools.App.csproj -c Release --no-restore
```

转换脚本针对当前纯路径 SVG 和既定视口；遇到新的变换、样式或图形结构会拒绝转换，需要先核对转换方式。不要直接修改生成文件中的曲线，否则下次同步会覆盖。

## 验证

- Release 构建通过，0 警告、0 错误。
- `scripts/test-app-smoke.ps1` 通过，覆盖真实 WPF 窗口、侧边栏物理拖动、收起/展开、应用复用、后台重启和清理退出。
- 已查看实际截图，主页面彩色标识及绿色侧边栏中的白色标识均正常显示；本机截图为 150% 缩放。
- 从构建完成的 EXE 提取出的 48×48 图标为新标志。

本机证据：

- [回归检查结果](../.artifacts/app-smoke/c2949ec4866940a6a5385e377cc558bf/summary.json)
- [主页面](../.artifacts/app-smoke/c2949ec4866940a6a5385e377cc558bf/home.png)
- [侧边栏](../.artifacts/app-smoke/c2949ec4866940a6a5385e377cc558bf/quick.png)
- [EXE 提取图标](../.artifacts/app-smoke/c2949ec4866940a6a5385e377cc558bf/executable-icon.png)

本次仅接入主应用品牌资源，没有更改录制、计划调度或 ClassIsland 桥接逻辑，也未创建安装包或修改已有桌面快捷方式。
