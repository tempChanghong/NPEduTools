import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, cp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

// Real ExamAware installs under userData, outside the plugin development node_modules tree.
test('installed main entry loads without host or development SDK module resolution', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'NPEduTools.ExamAware.Installed.'));
  try {
    const entry = join(directory, 'index.cjs');
    await cp(new URL('../dist/main/index.cjs', import.meta.url), entry);
    const { stdout } = await promisify(execFile)(process.execPath, ['-e',
      "const entry=require(process.argv[1]).default; console.log(JSON.stringify({type:typeof entry,apiVersion:entry.apiVersion,process:entry.process}))", entry],
      { cwd: directory, env: { ...process.env, NODE_PATH: '', NODE_OPTIONS: '' }, timeout: 10000, windowsHide: true });
    assert.deepEqual(JSON.parse(stdout), { type: 'function', apiVersion: 2, process: 'main' });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
