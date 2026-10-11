import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtemp, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, dirname, basename, resolve} from 'node:path';
import {createPlan, readOptions, readTapEvidence, readTrxEvidence, readBrowserEvidence, readDatabaseEvidence, databaseTransportScenarios, childEnvironment, sourceFingerprints} from '../scripts/test-npep-operations.mjs';
import {runStages, testEnvironment} from '../scripts/test-npep-n4.mjs';

const roots = {desktop: 'desktop', web: 'web', backend: 'backend'};
const tap = (passed = 3, skipped = 0) => `TAP version 13\n1..${passed + skipped}\n# tests ${passed + skipped}\n# suites 0\n# pass ${passed}\n# fail 0\n# cancelled 0\n# skipped ${skipped}\n# todo 0\n# duration_ms 1\n`;
const trx = '<Counters total="3" executed="3" passed="3" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" />';
const browser = () => ({stats: {expected: 1, unexpected: 0, skipped: 0, flaky: 0}, suites: [{suites: [{specs: [{title: 'NPEP school overview download',
  tests: [{expectedStatus: 'passed', status: 'expected', results: [{status: 'passed'}]}]}]}]}]});

test('operations plan distinguishes preflight, selected checks and native database scope without Docker or production tools', () => {
  const plan = createPlan(roots, 'result');
  assert.equal(plan.find(item => item.id === 'browser').status, 'NOT_REQUESTED');
  assert.equal(plan.find(item => item.id === 'database').status, 'NOT_REQUESTED');
  assert.ok(createPlan(roots, 'result', {browser: true, database: true}).every(item => item.requested));
  assert.ok(plan.findIndex(item => item.id === 'desktop') < plan.findIndex(item => item.id === 'cross-contract'));
  assert.ok(plan.findIndex(item => item.id === 'cross-contract') < plan.findIndex(item => item.id === 'comparison-contract'));
  const text = JSON.stringify(plan);
  assert.ok(!/docker|deploy-agent|upgrade\.sh|rollback\.sh/i.test(text));
  assert.ok(!text.includes('newfires.top'));
  assert.ok(!text.includes('--skip-desktop'));
  const database = createPlan(roots, 'result', {database: true}).find(item => item.id === 'database');
  assert.deepEqual(database.args, ['scripts/run-native-npep-tests.js', '--desktop-root', roots.desktop, '--test-reporter', 'tap']);
  assert.deepEqual(readOptions(['--check', '--browser', '--web-root', 'somewhere']), {check: true, browser: true, database: false, 'web-root': 'somewhere'});
  for (const args of [['--web-root'], ['--production'], ['--backend-root', '--browser']]) assert.throws(() => readOptions(args));
});

test('operations acceptance requires real test counters, rejecting exit-zero empty, skipped and malformed TAP', () => {
  assert.equal(readTapEvidence(tap()).groups[0].passed, 3);
  assert.equal(readTapEvidence(tap(3, 1), {allowSkipped: true}).groups[0].skipped, 1);
  assert.equal(readTapEvidence(tap() + tap(6)).groups.length, 2);
  for (const text of ['', 'exit zero', tap(0), tap(3, 1), tap().replace('# fail 0', '# fail 1'),
    tap().replace('# todo 0', '# todo 1'), tap().replace('# tests 3', '# tests 4'), tap().replace('# pass 3\n', '')]) {
    assert.throws(() => readTapEvidence(text));
  }
});

test('operations desktop and browser evidence reject empty, skipped, flaky and expected failure scenarios', () => {
  assert.equal(readTrxEvidence(trx).passed, 3);
  for (const text of ['', trx.replace('passed="3"', 'passed="0"'), trx.replace('notExecuted="0"', 'notExecuted="1"')]) {
    assert.throws(() => readTrxEvidence(text));
  }
  assert.equal(readBrowserEvidence(browser()).expected, 1);
  for (const key of ['unexpected', 'skipped', 'flaky']) {
    const value = browser(); value.stats[key] = 1; assert.throws(() => readBrowserEvidence(value));
  }
  const missing = browser(); missing.suites = []; assert.throws(() => readBrowserEvidence(missing));
  const fake = browser(); fake.suites[0].suites[0].specs[0].tests[0].expectedStatus = 'failed'; assert.throws(() => readBrowserEvidence(fake));
});

test('failed execution evidence persists a failure and prevents running later suites', async () => {
  const plan = createPlan(roots, 'result'), persisted = [], visited = [];
  await assert.rejects(runStages(plan, async stage => {
    visited.push(stage.id);
    if (stage.id === 'desktop') readTrxEvidence('missing');
    return 0;
  }, async () => persisted.push(structuredClone(plan))), /Missing TRX/);
  assert.deepEqual(visited, ['gate-regressions', 'desktop']);
  assert.equal(plan[1].status, 'FAILED'); assert.equal(plan[2].status, 'NOT_RUN');
  assert.equal(persisted.at(-1)[1].status, 'FAILED');
});

test('operations child environment drops real database, credentials and integration endpoints', () => {
  const result = testEnvironment({Path: 'safe', DATABASE_URL: 'PRODUCTION_SECRET', PGHOST: 'prod',
    VITE_DEFAULT_KV_SERVER: 'https://production.invalid', JWT_SECRET: 'SECRET', NPEP_ENABLED: 'true',
    NPEP_N3_ACCEPTANCE_DLL: 'unwanted'}, 'empty.env');
  assert.equal(result.Path, 'safe'); assert.equal(result.DOTENV_CONFIG_PATH, 'empty.env');
  assert.equal(result.RUN_DATABASE_TESTS, 'false');
  assert.ok(!JSON.stringify(result).includes('SECRET')); assert.ok(!JSON.stringify(result).includes('production'));
  assert.equal(result.NPEP_N3_ACCEPTANCE_DLL, undefined);
});

test('operations browser uses production build semantics and its own loopback ports instead of development .env routing', () => {
  const base = testEnvironment({Path: 'safe', NODE_ENV: 'development', VITE_SERVER_URL: 'https://production.invalid'}, 'empty.env');
  const env = childEnvironment(base, {evidence: 'browser'}, 'results', {web: 53000, api: 53001});
  assert.equal(env.NODE_ENV, 'production'); assert.equal(env.E2E_WEB_PORT, '53000'); assert.equal(env.E2E_API_PORT, '53001');
  assert.ok(!JSON.stringify(env).includes('production.invalid'));
  assert.equal(childEnvironment(base, {evidence: 'tap'}, 'results', null).NODE_ENV, 'test');
  const comparison = childEnvironment(base, {evidence: 'comparison'}, 'results', null);
  assert.ok(comparison.NPEP_DIAGNOSTIC_COMPARISON_CASES.endsWith('comparison-cases.json'));
  assert.equal(childEnvironment(base, {evidence: 'tap'}, 'results', null).NPEP_DIAGNOSTIC_COMPARISON_CASES, undefined);
});

test('database evidence requires every real .NET transport scenario, actual overview SQL and cleanup with zero skips', () => {
  const scenarios = databaseTransportScenarios.map((name, index) => `    ok ${index + 2} - ${name}\n`).join('');
  const success = 'ok 1 - school device overview: real SQL totals, pagination, name search, current binding and authorization\n' +
    scenarios + tap(6) + 'Native NPEP acceptance passed; temporary cluster removed.\n';
  const evidence = readDatabaseEvidence(success);
  assert.equal(evidence.groups[0].passed, 6);
  assert.deepEqual(evidence.executedTransportScenarios, databaseTransportScenarios);
  assert.equal(evidence.realHttp, true); assert.equal(evidence.simulatedSystemEffects, true);
  // Test all five omissions; zero exit and positive counters are insufficient.
  for (const name of databaseTransportScenarios) {
    for (const replacement of ['', ' # SKIP', ' # TODO']) {
      const value = replacement ? success.replace(name + '\n', name + replacement + '\n') :
        success.replace(new RegExp(`^.*${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\n`, 'm'), '');
      assert.throws(() => readDatabaseEvidence(value));
    }
    assert.throws(() => readDatabaseEvidence(success + `ok 99 - ${name}\n`));
  }
  for (const value of [success.replace('ok 1 - school', 'not ok 1 - school'),
    success.replace(tap(6), tap(5, 1)),
    success.replace('temporary cluster removed.', 'temporary cluster still running.')]) assert.throws(() => readDatabaseEvidence(value));
});

test('source fingerprint detects uncommitted HTML/SQL drift and excludes ignored credentials', async t => {
  const base = resolve(tmpdir()), directory = await mkdtemp(join(base, 'npep-operations-fingerprint-'));
  t.after(async () => {
    assert.equal(dirname(resolve(directory)), base);
    assert.ok(basename(directory).startsWith('npep-operations-fingerprint-'));
    await rm(directory, {recursive: true, force: true});
  });
  assert.equal(spawnSync('git', ['init', directory], {windowsHide: true}).status, 0);
  await writeFile(join(directory, '.gitignore'), 'ignored-credentials.json\n');
  await writeFile(join(directory, 'ignored-credentials.json'), 'SYNTHETIC_SECRET');
  await writeFile(join(directory, 'index.html'), '<main>original</main>');
  await writeFile(join(directory, 'migration.sql'), 'SELECT 1;');
  const initial = await sourceFingerprints({desktop: directory});
  assert.deepEqual(initial.desktop.inputs.map(item => item.path), ['index.html', 'migration.sql']);
  await writeFile(join(directory, 'ignored-credentials.json'), 'SYNTHETIC_CHANGED_SECRET');
  assert.equal((await sourceFingerprints({desktop: directory})).desktop.sha256, initial.desktop.sha256);
  await writeFile(join(directory, 'migration.sql'), 'SELECT 2;');
  const sql = await sourceFingerprints({desktop: directory});
  assert.notEqual(sql.desktop.sha256, initial.desktop.sha256);
  await writeFile(join(directory, 'index.html'), '<main>changed</main>');
  assert.notEqual((await sourceFingerprints({desktop: directory})).desktop.sha256, sql.desktop.sha256);
});
