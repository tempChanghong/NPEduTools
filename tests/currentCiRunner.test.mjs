import {test} from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync, spawnSync} from 'node:child_process';
import {mkdtempSync, mkdirSync, writeFileSync, readdirSync, readFileSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';

test('Windows PowerShell 5.1 records failed source gates without executing acceptance', {skip: process.platform !== 'win32'}, () => {
  const root = mkdtempSync(join(tmpdir(), 'npep-current-gate-'));
  const driver = fileURLToPath(new URL('../scripts/test-npep-current.ps1', import.meta.url));
  const roots = {}, commits = {};
  try {
    for (const key of ['desktop', 'web', 'backend']) {
      const path = roots[key] = join(root, key);
      mkdirSync(path);
      if (key === 'desktop') {
        mkdirSync(join(path, 'src/NPEduTools.App'), {recursive: true});
        writeFileSync(join(path, 'src/NPEduTools.App/NPEduTools.App.csproj'), '<Project />');
      } else writeFileSync(join(path, 'package.json'), JSON.stringify({name: key === 'web' ? 'classworks' : 'ClassworksKV'}));
      const git = args => execFileSync('git', ['-C', path, ...args], {encoding: 'utf8', windowsHide: true}).trim();
      git(['init', '--quiet']); git(['add', '.']);
      git(['-c', 'user.name=CI fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'fixture']);
      commits[key] = git(['rev-parse', 'HEAD']);
    }
    const manifest = join(root, 'manifest.json');
    writeFileSync(manifest, JSON.stringify({schemaVersion: 1, category: 'CURRENT',
      sources: Object.fromEntries(Object.keys(roots).map(key => [key, {commit: '0'.repeat(40)}]))}));
    const cases = [
      ['sha-mismatch', ['-DesktopCommit', '0'.repeat(40)], 1],
      ['manifest-mismatch', ['-SourceManifest', manifest], 1],
      ['missing-root', ['-DesktopRoot', join(root, 'missing')], 0],
    ];
    for (const [name, overrides, stages] of cases) {
      const output = join(root, name);
      const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', driver,
        '-DesktopRoot', roots.desktop, '-WebRoot', roots.web, '-BackendRoot', roots.backend, '-ResultsRoot', output];
      // Named PowerShell arguments may not be repeated.
      if (name === 'missing-root') args[args.indexOf('-DesktopRoot') + 1] = overrides[1];
      else args.push(...overrides);
      const result = spawnSync('powershell.exe', args, {encoding: 'utf8', timeout: 30000, windowsHide: true});
      assert.ifError(result.error);
      assert.notEqual(result.status, 0, name);
      const run = readdirSync(output);
      assert.equal(run.length, 1);
      const record = JSON.parse(readFileSync(join(output, run[0], 'result.json'), 'utf8'));
      assert.equal(record.status, 'FAILED', name);
      assert.equal(record.scope, 'SELECTED_AUTOMATED');
      assert.equal(record.stages.length, stages, name);
      assert(record.finishedAt && record.error);
      assert.equal(record.deviceAcceptance, 'NOT_RUN');
      assert.equal(record.deployment, 'NOT_RUN');
      if (name === 'sha-mismatch') assert.equal(record.stages[0].exitCode, 1);
      if (name === 'manifest-mismatch') assert.equal(record.stages[0].status, 'PASSED');
    }
  } finally { rmSync(root, {recursive: true, force: true}); }
});
