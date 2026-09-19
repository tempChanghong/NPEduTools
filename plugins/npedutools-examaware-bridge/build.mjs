import { build } from 'esbuild';
await build({ entryPoints: ['src/main.ts'], bundle: true, platform: 'node', format: 'cjs',
  target: 'node22', outfile: 'dist/main/index.cjs' });
await build({ entryPoints: ['src/renderer.ts'], bundle: true, platform: 'browser', format: 'esm',
  target: 'es2022', outfile: 'dist/renderer/index.mjs' });
