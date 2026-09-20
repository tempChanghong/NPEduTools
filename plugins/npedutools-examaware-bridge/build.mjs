import { build } from 'esbuild';
import { writeFile } from 'node:fs/promises';
const main = await build({ metafile: true, entryPoints: ['src/main.ts'], bundle: true, platform: 'node', format: 'cjs',
  target: 'node22', outfile: 'dist/main/index.cjs' });
const renderer = await build({ metafile: true, entryPoints: ['src/renderer.ts'], bundle: true, platform: 'browser', format: 'esm',
  target: 'es2022', outfile: 'dist/renderer/index.mjs' });
await writeFile('dist/bundle-inputs.json', JSON.stringify({ main: main.metafile, renderer: renderer.metafile }, null, 2));
