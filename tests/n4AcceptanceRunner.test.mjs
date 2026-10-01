import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createPlan, runStages, verifyContractGroups, testEnvironment} from '../scripts/test-npep-n4.mjs';

const roots = {desktop: 'desktop', web: 'web', backend: 'backend'};
test('one N4 plan covers both manual and schedule suites without duplicate test files', () => {
  const stages = createPlan(roots);
  assert.equal(stages.filter(stage => stage.requested).length, 4);
  for (const id of ['backend', 'web']) {
    const files = stages.find(stage => stage.id === id).args.filter(arg => arg.endsWith('.test.js'));
    assert.equal(new Set(files).size, files.length);
  }
  assert.equal(stages.find(stage => stage.id === 'database').status, 'NOT_REQUESTED');
  assert.equal(stages.find(stage => stage.id === 'browser-manual').status, 'NOT_REQUESTED');
  assert.equal(createPlan(roots, {browser: true, database: true}).filter(stage => stage.requested).length, 7);
});

test('failure records the failed phase, stops subsequent phases and never claims unrequested work passed', async () => {
  const stages = createPlan(roots); const launched = [], saved = [];
  await assert.rejects(runStages(stages, stage => {launched.push(stage.id); return stage.id === 'transport' ? 1 : 0;},
    async () => {saved.push(stages.map(stage => stage.status));}), /transport exited with 1/);
  assert.deepEqual(launched, ['desktop', 'transport']);
  assert.equal(stages[0].status, 'PASSED'); assert.equal(stages[1].status, 'FAILED');
  assert.equal(stages[2].status, 'NOT_RUN'); assert.equal(stages[6].status, 'NOT_REQUESTED');
  assert.equal(saved[0][0], 'RUNNING'); assert.equal(saved.at(-1)[1], 'FAILED');
});

test('spawn errors and signal termination cannot turn into successful phases', async () => {
  for (const executor of [() => {throw new Error('missing tool');}, () => null]) {
    const stages = createPlan(roots);
    await assert.rejects(runStages(stages, executor));
    assert.equal(stages[0].status, 'FAILED'); assert.equal(stages[1].status, 'NOT_RUN');
  }
});

test('successful selected phases preserve browser/database as unrequested', async () => {
  const stages = createPlan(roots); await runStages(stages, () => 0);
  assert.equal(stages.filter(stage => stage.status === 'PASSED').length, 4);
  assert.equal(stages.filter(stage => stage.status === 'NOT_REQUESTED').length, 3);
});

test('shared contracts fail before execution if copies differ or are missing', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'n4-contract-test-')); t.after(() => rm(directory, {recursive: true}));
  const a = join(directory, 'a.json'), b = join(directory, 'b.json');
  await writeFile(a, '{}'); await writeFile(b, '{}');
  assert.equal((await verifyContractGroups([[a, b]]))[0].sha256.length, 64);
  await writeFile(b, '{"changed":true}');
  await assert.rejects(verifyContractGroups([[a, b]]), /contract mismatch/);
  await assert.rejects(verifyContractGroups([[a, join(directory, 'missing.json')]]));
});

test('children receive system paths but no caller database credentials or application environment', () => {
  const env = testEnvironment({Path: 'tools', USERPROFILE: 'user', DOTNET_ROOT: 'sdk', NPEP_TEST_PG_BIN: 'pg',
    DATABASE_URL: 'production', PGPASSWORD: 'secret', PGHOST: 'remote', NPEP_ENABLED: 'true', JWT_SECRET: 'secret',
    DOTENV_CONFIG_PATH: 'production.env', NODE_OPTIONS: '--require=other', VITE_SERVER_URL: 'https://school'}, 'empty.env');
  assert.equal(env.Path, 'tools'); assert.equal(env.NPEP_TEST_PG_BIN, 'pg'); assert.equal(env.DOTENV_CONFIG_PATH, 'empty.env');
  for (const key of ['DATABASE_URL', 'PGPASSWORD', 'PGHOST', 'NPEP_ENABLED', 'JWT_SECRET', 'NODE_OPTIONS', 'VITE_SERVER_URL'])
    assert.equal(env[key], undefined);
  assert.equal(env.NODE_ENV, 'test'); assert.equal(env.RUN_DATABASE_TESTS, 'false');
});
