import { defineRendererPlugin } from '@dsz-examaware/plugin-sdk';
import { pairing } from './pairing.js';

export default defineRendererPlugin({ activate(ctx) {
  ctx.scope.add(ctx.api.ui.home.register({
    id: 'npedutools-pair', label: '连接 NPEduTools', icon: 'link',
    hint: '导入配对文件，连接状态查询、正常退出与登录自启动设置。',
    async action() {
      try {
        const paths = await ctx.api.files.open({ title: '选择 NPEduTools 配对文件', filters: [{ name: '配对文件', extensions: ['json'] }] });
        if (!paths[0]) return;
        const stat = await ctx.api.files.stat(paths[0]);
        if (!stat || stat.size > 4096) throw new Error('无效的配对文件。');
        const config = pairing(JSON.parse(await ctx.api.files.readText(paths[0])));
        if (!config) throw new Error('配对文件格式不正确，请从 NPEduTools 重新导出。');
        await ctx.api.settings.replace({ ...config });
        await ctx.api.dialogs.message({ type: 'info', title: 'NPEduTools 配对', message: '配对信息已保存。', detail: 'NPEduTools 可查询状态、发起正常退出，并在你确认后设置登录自启动。配对本身不修改启动登记。' });
      } catch {
        await ctx.api.dialogs.message({ type: 'error', title: '配对失败', message: '未能导入配对文件，请从 NPEduTools 重新导出后重试。' });
      }
    }
  }));
  ctx.scope.add(ctx.api.ui.home.register({
    id: 'npedutools-unpair', label: '断开 NPEduTools', icon: 'unlink', hint: '清除本插件保存的配对信息。',
    async action() { await ctx.api.settings.replace({}); }
  }));
} });
