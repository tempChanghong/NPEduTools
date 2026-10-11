import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtemp, mkdir, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve, dirname, basename} from 'node:path';
import {resolveOperationsCiSources} from '../scripts/resolve-npep-operations-ci.mjs';
import {inspectOperationsSources, readOptions} from '../scripts/test-npep-operations.mjs';

const desktop = 'a'.repeat(40), web = 'b'.repeat(40), backend = 'c'.repeat(40);
const record = {schemaVersion: 1, web_sha: web, backend_sha: backend};

test('CI source resolution accepts pinned defaults and explicit overrides; rejects branches, short hashes and environment injection', () => {
  assert.deepEqual(resolveOperationsCiSources(record, {desktop}), {desktop, web, backend});
  assert.deepEqual(resolveOperationsCiSources(record, {desktop, web: desktop.toUpperCase(), backend: web}), {desktop, web: desktop, backend: web});
  for (const bad of ['main', 'codex/test', '1234567', '', undefined, ' ' + web, web + '\nOTHER=value']) {
    assert.throws(() => resolveOperationsCiSources(record, {desktop: bad}));
    if (bad) assert.throws(() => resolveOperationsCiSources(record, {desktop, web: bad}));
  }
  for (const value of [null, {...record, schemaVersion: 2}, {...record, web_sha: 'main'}, {...record, repository: 'other'}]) {
    assert.throws(() => resolveOperationsCiSources(value, {desktop, web, backend}));
  }
  const options = readOptions(['--require-clean', '--desktop-commit', desktop, '--web-commit', web, '--backend-commit', backend]);
  assert.equal(options['require-clean'], true); assert.equal(options['desktop-commit'], desktop);
  assert.throws(() => readOptions(['--web-commit', '--browser']));
});

test('CI requires actual clean repositories at all three specified commits before dependency or build checks', async t => {
  const base = resolve(tmpdir()), directory = await mkdtemp(join(base, 'npep-operations-ci-'));
  t.after(async () => {
    assert.equal(dirname(resolve(directory)), base); assert.ok(basename(directory).startsWith('npep-operations-ci-'));
    await rm(directory, {recursive: true, force: true});
  });
  const roots = {}, settings = {'require-clean': true};
  function git(root, ...args) {
    const value = spawnSync('git', ['-C', root, ...args], {windowsHide: true, encoding: 'utf8'});
    assert.equal(value.status, 0, value.stderr); return value.stdout.trim();
  }
  for (const key of ['desktop', 'web', 'backend']) {
    const root = join(directory, key); roots[key] = root; await mkdir(root);
    git(root, 'init');
    await writeFile(join(root, 'package.json'), JSON.stringify({name: key === 'web' ? 'classworks' : 'ClassworksKV'}));
    if (key === 'desktop') {
      await mkdir(join(root, 'src/NPEduTools.App'), {recursive: true});
      await writeFile(join(root, 'src/NPEduTools.App/NPEduTools.App.csproj'), '<Project />');
    }
    git(root, 'add', '.'); git(root, '-c', 'user.name=Operations Test', '-c', 'user.email=operations-test@example.invalid', 'commit', '-m', 'synthetic source');
    settings[`${key}-commit`] = git(root, 'rev-parse', 'HEAD');
  }
  assert.equal((await inspectOperationsSources(roots, settings)).immutable, true);
  await assert.rejects(inspectOperationsSources(roots, {...settings, 'web-commit': desktop}), /commit mismatch/);
  const missing = {...settings}; delete missing['backend-commit'];
  await assert.rejects(inspectOperationsSources(roots, missing), /explicit commit/);
  await assert.rejects(inspectOperationsSources(roots, {...settings, 'backend-commit': 'main'}), /full SHA/);
  await writeFile(join(roots.web, 'untracked.vue'), '<template>changed</template>');
  await assert.rejects(inspectOperationsSources(roots, settings), /uncommitted changes/);
  assert.equal((await inspectOperationsSources(roots)).localChanges, true);
  await assert.rejects(inspectOperationsSources({...roots, backend: roots.web}, settings), /distinct repository/);
});
