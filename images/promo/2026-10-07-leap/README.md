# NPEduTools「跃迁」宣传图

2026-10-07。横幅主视觉，2560 × 1080，RGB。重点为视觉冲击：墨绿背景、超大 MiSans Heavy 标题、真实界面叠放、光轨和玻璃字；无功能说明文案。

## 当前修订：v4

最终导出使用 `NPEduTools-跃迁-v4.png`、`NPEduTools-跃迁-v4.ai`、`NPEduTools-跃迁-v4-分层.psd`。在 v3 上将三张截图作为整体向右移动 72 px；品牌矢量字、标题及辅助文字沿用 v3。前三版均保留。

脚本为 `revise-screens-v4.jsx`、`finish-photoshop-v4.jsx`，核验记录为 `layout-v4-verification.txt`、`photoshop-v4-verification.txt`。

v4 AI、PNG、分层 PSD 已保存并核验；PSD 画布为 2560 × 1080，保留 9 个主要图层或组、87 个图层及组对象、3 个可编辑文字图层。已目视检查 AI 与 PSD 导出预览。

## 上次修订：v3

建议使用 `NPEduTools-跃迁-v3.png`、`NPEduTools-跃迁-v3.ai`、`NPEduTools-跃迁-v3-分层.psd`。顶部「星火动力」已替换为 `D:/Archives/文献集/NP/LOGO/星火动力中文字黑色.svg` 的原版矢量字，等比缩放并改为浅薄荷色，字形未拉伸。

「跃迁」、下方产品标志字组及底部玻璃字均左移 64 px；标题右边界约为 809 px，已与左侧倾斜截图拉开距离。网址保留 v2 的自然字形比例。AI 保留 9 个图层、4 个活动文字对象；品牌字为矢量路径。v1、v2 文件均保留。

本次脚本为 `revise-layout-v3.jsx`、`finish-photoshop-v3.jsx`；核验记录为 `layout-v3-verification.txt`、`photoshop-v3-verification.txt`。

v3 分层 PSD 已在 Photoshop 保存为 2560 × 1080，保留 9 个主要图层或组、87 个图层及组对象、3 个可编辑文字图层；品牌矢量字在 PSD 中为栅格内容，矢量编辑使用 AI 母版。已目视检查 AI 与 PSD 的导出预览。

## 上次修订：v2

建议使用 `NPEduTools-跃迁-v2.png`、`NPEduTools-跃迁-v2.ai`、`NPEduTools-跃迁-v2-分层.psd`。恢复「星火动力」、`novark.ink` 和小号 `NPEduTools` 的原生字形比例，横向/纵向缩放均为 100%；仅调整字号、字距及位置。字号依次为 37、34、45 pt，仍使用 MiSans Bold、Regular、Demibold。标题、背景和界面构图沿用首版。

首版文件保留供对比。`revise-typography.jsx` 为 AI 修订脚本，`finish-photoshop-v2.jsx` 为对应 PSD 整理脚本，核验记录见 `typography-v2-verification.txt` 和 `photoshop-v2-verification.txt`。基础 `build-poster.jsx` 也已修正三处辅助文字的布局方式，避免以后重建时重新拉伸。

## 交付文件

- `NPEduTools-跃迁.png`：Illustrator 导出的最终预览，画板裁切，2560 × 1080。
- `NPEduTools-跃迁.ai`：主要可编辑母版，9 个命名设计图层、5 个活动文字对象。标题、品牌名称及网址可编辑；底部 NPEDUTOOLS 为渐变矢量轮廓。截图已嵌入，标志为矢量路径。
- `NPEduTools-跃迁-分层.psd`：已在 Photoshop 中打开并裁切至 2560 × 1080；保留 9 个主要设计图层或组、84 个图层及组对象、4 个可编辑文字图层。渐变、光轨、标志和玻璃字在 PSD 中为栅格内容，矢量编辑请使用 AI。
- `NPEduTools-跃迁-PS.png`：由分层 PSD 导出的比对预览。与 AI 版构图一致，Adobe 对倾斜截图的重采样略有差异。
- `NPEduTools-跃迁.psd`：Illustrator 直接导出的中间文件，包含画板外光晕，3346 × 1425。后续编辑优先使用带「分层」的裁切版。
- `build-poster.jsx`、`finish-photoshop.jsx`：原生 Illustrator / Photoshop 制作脚本。
- `build-log.txt`、`photoshop-verification.txt`：本次生成及分层检查记录。

## 素材

星火动力图形来自 `D:/Archives/文献集/NP/LOGO/星火动力0702.svg`，以白色适配深底；产品标志来自本仓库 `images/branding/npedutools-logo.svg`。

三张真实界面截图分别来自 `.artifacts/home-layout/home-1040x760.png`、`.artifacts/workspace-ui/settings-compact.png` 和 `.artifacts/app-smoke/c2949ec4866940a6a5385e377cc558bf/quick.png`。这些为已有本地开发截图，画面用于宣传构图草案，不代表新版本发布状态。

## 检查

在 Illustrator 30.8.2 中制作并保存 AI，导出 PNG 与分层 PSD。在 Photoshop 27.9.1 中打开 PSD，核对分层并将画布裁切至与 AI 画板一致。已目视检查 AI 导出预览、PSD 导出预览和两个应用中的画面。没有更改程序代码，也没有对外发布。
