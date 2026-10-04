// Local synthetic operations acceptance. No Docker, deployment or real device effects.
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createHash, randomUUID} from 'node:crypto';
import {openSync, closeSync} from 'node:fs';
import {readFile, writeFile, mkdir, realpath, access, rename, readdir} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {join, resolve} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import net from 'node:net';
import {inspectPairingSources} from './check-npep-pairing-sources.mjs';
import {runStages, testEnvironment, verifyContractGroups} from './test-npep-n4.mjs';

const desktopRoot = fileURLToPath(new URL('../', import.meta.url));
export function readOptions(args) {
  const result = {browser: false, database: false, check: false};
  for (let i = 0; i < args.length; i++) {
    if (['--browser', '--database', '--check'].includes(args[i])) result[args[i].slice(2)] = true;
    else if (['--web-root', '--backend-root', '--powershell-version'].includes(args[i]) && args[i + 1] && !args[i + 1].startsWith('--')) {
      result[args[i].slice(2)] = args[++i];
    } else throw new Error(`Unknown or incomplete operations option: ${args[i]}`);
  }
  return result;
}

export function createPlan(roots, directory, {browser = false, database = false} = {}) {
  const stage = (id, repo, command, args, evidence, requested = true) => ({id, repo, cwd: roots[repo], command, args,
    evidence, requested, status: requested ? 'NOT_RUN' : 'NOT_REQUESTED'});
  const nodeTests = (id, repo, files) => stage(id, repo, process.execPath, ['--test', '--test-reporter=tap', '--test-concurrency=1', ...files], 'tap');
  const builds = join(directory, 'build'), results = join(directory, 'test-results');
  return [
    nodeTests('gate-regressions', 'desktop', ['tests/operationsAcceptance.test.mjs']),
    stage('desktop', 'desktop', 'dotnet', ['test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', builds, '-m:1',
      '-p:RestoreLockedMode=true', '--verbosity', 'quiet', '--filter',
      'FullyQualifiedName~NpepDiagnosticsTests|FullyQualifiedName~NpepConnectionSessionTests|FullyQualifiedName~CachedHostStatusTests|FullyQualifiedName~NpepOperationsFixtureTests',
      '--logger', 'trx;LogFileName=operations.trx', '--results-directory', results], 'trx'),
    stage('cross-contract', 'desktop', process.execPath, ['scripts/check-npep-operations-contract.mjs', roots.desktop, roots.web,
      roots.backend, join(directory, 'fixtures'), join(directory, 'contract-result.json')], 'contract'),
    stage('wpf-build', 'desktop', 'dotnet', ['build', 'src/NPEduTools.App/NPEduTools.App.csproj', '--artifacts-path', builds, '-m:1',
      '-p:RestoreLockedMode=true', '--verbosity', 'quiet'], 'build'),
    nodeTests('backend', 'backend', ['tests/npepDeviceOverview.test.js', 'tests/npepWire.test.js', 'tests/npepHttpErrors.test.js']),
    nodeTests('web', 'web', ['tests/npepDeviceOverview.test.js', 'tests/npepDiagnostics.test.js']),
    nodeTests('web-flows', 'web', ['tests/npepDeviceOverviewFlows.test.js', 'tests/npepAdminClientFlows.test.js']),
    stage('browser', 'web', process.execPath, [join(roots.web, 'node_modules/@playwright/test/cli.js'), 'test',
      'tests/e2e/admin-screens.spec.js', '--grep', 'NPEP', '--reporter=json'], 'browser', browser),
    stage('database', 'backend', process.execPath, ['scripts/run-native-npep-tests.js', '--skip-desktop', '--test-reporter', 'tap'], 'database', database),
  ];
}

export function readTapEvidence(text, {allowSkipped = false} = {}) {
  const groups = [];
  for (const block of text.split(/^# tests /m).slice(1)) {
    const count = key => Number(block.match(new RegExp(`^# ${key} (\\d+)\\s*$`, 'm'))?.[1]);
    const value = {total: Number(block.match(/^\d+/)?.[0]), passed: count('pass'), failed: count('fail'),
      skipped: count('skipped'), cancelled: count('cancelled'), todo: count('todo')};
    assert.ok(Object.values(value).every(Number.isSafeInteger), 'Missing TAP counters');
    assert.ok(value.passed > 0 && value.failed === 0 && value.cancelled === 0 && value.todo === 0, 'TAP run is not an executed pass');
    assert.equal(value.total, value.passed + value.skipped, 'Inconsistent TAP counters');
    if (!allowSkipped) assert.equal(value.skipped, 0, 'Requested tests were skipped');
    groups.push(value);
  }
  assert.ok(groups.length > 0, 'Missing TAP evidence; exit zero alone is insufficient');
  return {groups, skippedTests: text.split(/\r?\n/).filter(line => /^\s*ok \d+ - .*# SKIP/.test(line)).map(line => line.trim())};
}

export function readTrxEvidence(text) {
  const counters = text.match(/<Counters\s+([^>]+)\/>/)?.[1];
  assert.ok(counters, 'Missing TRX counters');
  const values = Object.fromEntries([...counters.matchAll(/(\w+)="(\d+)"/g)].map(([, key, count]) => [key, Number(count)]));
  assert.ok(values.total > 0 && values.passed > 0, 'Empty desktop test execution');
  assert.equal(values.total, values.passed, 'Desktop tests failed or were not executed');
  assert.equal(values.executed, values.passed, 'Desktop executed count mismatch');
  for (const key of ['failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted']) assert.equal(values[key], 0, `Non-pass desktop result: ${key}`);
  return values;
}

export function readDatabaseEvidence(text) {
  const evidence = readTapEvidence(text, {allowSkipped: true});
  const allowed = [
    'Noise screen authentication isolation and actual .NET HTTP/PostgreSQL monitoring',
    'N3 actual .NET transport through real HTTP/PostgreSQL (simulated OS effects)',
    'N4 actual .NET prepare/explicit start over HTTP/PostgreSQL (simulated player)',
    'Web pairing: actual .NET client claims, confirms, restarts, reports and revokes',
    'N4.3c actual .NET scheduler and 0.6/0.7 transport through disposable SQL',
  ];
  for (const line of evidence.skippedTests) {
    const name = line.match(/^ok \d+ - (.*?)\s+# SKIP/)?.[1];
    assert.ok(allowed.includes(name), `Unexpected database skip: ${name}`);
  }
  assert.equal(evidence.skippedTests.length, evidence.groups.reduce((sum, item) => sum + item.skipped, 0), 'Missing skipped database case names');
  assert.match(text, /^\s*ok \d+ - school device overview: .*authorization\s*$/m, 'Actual overview SQL scenario was not an executed pass');
  assert.ok(text.includes('Native NPEP acceptance passed; temporary cluster removed.'), 'Temporary cluster cleanup was not confirmed');
  return evidence;
}

export function readBrowserEvidence(report) {
  assert.ok(report.stats?.expected > 0, 'No browser tests executed');
  for (const key of ['unexpected', 'skipped', 'flaky']) assert.equal(report.stats[key], 0, `Non-pass browser result: ${key}`);
  function specs(suite) { return [...(suite.specs || []), ...(suite.suites || []).flatMap(specs)]; }
  const overview = report.suites.flatMap(specs).find(item => item.title.startsWith('NPEP school overview'));
  assert.ok(overview?.tests?.length > 0, 'Operations overview/download browser scenario did not execute');
  for (const item of overview.tests) {
    assert.equal(item.expectedStatus, 'passed'); assert.equal(item.status, 'expected');
    assert.equal(item.results.at(-1)?.status, 'passed', 'Overview browser result is not a pass');
  }
  return report.stats;
}

export function childEnvironment(base, stage, directory, ports) {
  // Vite's build(mode=production) does not override an inherited NODE_ENV=test.
  // Keep DEV false so ignored local .env addresses cannot win over the E2E API.
  return {...base, ...(stage.evidence === 'browser' ? {
    NODE_ENV: 'production', PLAYWRIGHT_JSON_OUTPUT_FILE: join(directory, 'browser.json'),
    E2E_WEB_PORT: String(ports.web), E2E_API_PORT: String(ports.api),
  } : {})};
}

async function browserPorts() {
  const servers = [net.createServer(), net.createServer()];
  try {
    for (const server of servers) await new Promise((done, reject) => {
      server.once('error', reject); server.listen(0, '127.0.0.1', done);
    });
    return {web: servers[0].address().port, api: servers[1].address().port};
  } finally {
    for (const server of servers) if (server.listening) await new Promise(done => server.close(done));
  }
}

function capture(command, args, cwd) {
  const value = spawnSync(command, args, {cwd, windowsHide: true, encoding: 'utf8'});
  if (value.error || value.status !== 0) throw new Error(`${command} prerequisite check failed`);
  return value.stdout.trim();
}

// Fingerprints source/test/config files, including uncommitted and untracked text
// inputs. Build outputs and ignored local secrets are never traversed or exported.
export async function sourceFingerprints(roots) {
  const manifest = {};
  for (const [repo, root] of Object.entries(roots)) {
    const files = capture('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], root).split('\0')
      .filter(file => /\.(?:cs|csproj|props|targets|json|js|mjs|cjs|vue|xaml|ya?ml|ps1|lock|sln|config|ts|scss|css|prisma|sql|sh|html?|resx)$/.test(file));
    const hash = createHash('sha256');
    const inputs = [];
    for (const file of [...new Set(files)].sort()) {
      let sha256;
      try { sha256 = createHash('sha256').update(await readFile(join(root, file))).digest('hex'); }
      catch (error) { if (error.code !== 'ENOENT') throw error; sha256 = 'MISSING'; }
      inputs.push({path: file, sha256}); hash.update(file + '\0' + sha256 + '\n');
    }
    manifest[repo] = {sha256: hash.digest('hex'), fileCount: inputs.length, inputs};
  }
  return manifest;
}

async function preflight(roots, settings) {
  if (process.platform !== 'win32') throw new Error('Desktop operations acceptance requires Windows named pipes.');
  const sources = await inspectPairingSources(roots);
  for (const [repo, files] of [
    ['desktop', ['global.json', 'scripts/check-npep-operations-contract.mjs', 'tests/operationsAcceptance.test.mjs',
      'src/NPEduTools.App/NpepDiagnostics.cs', 'tests/NPEduTools.Tests/NpepOperationsFixtureTests.cs']],
    ['web', ['node_modules/vue/package.json', 'src/utils/npepDiagnostics.js', 'src/utils/npepDeviceOverview.js',
      'tests/npepDiagnostics.test.js', 'tests/npepDeviceOverviewFlows.test.js']],
    ['backend', ['node_modules/ajv/package.json', 'domain/npep/deviceOverview.js', 'tests/npepDeviceOverview.test.js']],
  ]) for (const file of files) await access(join(roots[repo], file));
  const contracts = await verifyContractGroups([[join(roots.desktop, 'docs/npep/n1-wire.schema.json'), join(roots.backend, 'domain/npep/wire.schema.json')]]);
  const runtime = {node: process.version, dotnet: capture('dotnet', ['--version'], roots.desktop), powershell: settings['powershell-version'] ?? null};
  if (settings.browser) {
    const require = createRequire(join(roots.web, 'package.json'));
    await access(require('@playwright/test').chromium.executablePath());
    await access(join(roots.web, 'tests/e2e/admin-screens.spec.js'));
  }
  let postgresBin = null;
  if (settings.database) {
    postgresBin = process.env.NPEP_TEST_PG_BIN ? resolve(process.env.NPEP_TEST_PG_BIN) : null;
    if (!postgresBin) {
      const base = join(process.env.ProgramFiles || 'C:/Program Files', 'PostgreSQL');
      for (const version of (await readdir(base)).sort((a, b) => Number(b) - Number(a))) {
        try { await access(join(base, version, 'bin/initdb.exe')); postgresBin = join(base, version, 'bin'); break; } catch { /* next installation */ }
      }
    }
    if (!postgresBin) throw new Error('Set NPEP_TEST_PG_BIN to installed native PostgreSQL binaries.');
    for (const file of ['initdb.exe', 'pg_ctl.exe', 'pg_dump.exe', 'pg_restore.exe']) await access(join(postgresBin, file));
    await access(join(roots.backend, 'generated/prisma/client.ts'));
  }
  return {sources, runtime, contracts, postgresBin};
}

async function main() {
  const settings = readOptions(process.argv.slice(2));
  const directory = join(desktopRoot, '.artifacts/npep-operations', `${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`);
  await mkdir(directory, {recursive: true});
  const report = {schemaVersion: 1, status: 'RUNNING', startedAt: new Date().toISOString(), mode: settings.check ? 'CHECK_ONLY' : 'TEST',
    selected: {browser: settings.browser, database: settings.database}, releaseReady: false,
    realMicrophone: false, realSoftwareControl: false, manualAcceptance: 'NOT_RUN', productionAcceptance: 'NOT_RUN', deployment: 'NOT_RUN', stages: []};
  const persist = async () => {
    await writeFile(join(directory, 'result.tmp.json'), JSON.stringify(report, null, 2) + '\n');
    await rename(join(directory, 'result.tmp.json'), join(directory, 'result.json'));
  };
  console.log(`NPEP_OPERATIONS_RESULT: ${join(directory, 'result.json')}`); await persist();
  try {
    const roots = {desktop: await realpath(desktopRoot), web: await realpath(resolve(settings['web-root'] || join(desktopRoot, '../NPClassworks'))),
      backend: await realpath(resolve(settings['backend-root'] || join(desktopRoot, '../NPClassworksKV')))};
    report.stages = createPlan(roots, directory, settings); await persist();
    report.preflight = {status: 'PASSED', ...await preflight(roots, settings)};
    const fingerprints = await sourceFingerprints(roots);
    await writeFile(join(directory, 'source-inputs.json'), JSON.stringify(fingerprints, null, 2) + '\n');
    report.sourceFingerprints = Object.fromEntries(Object.entries(fingerprints).map(([key, value]) => [key, {sha256: value.sha256, fileCount: value.fileCount}]));
    await persist();
    if (!settings.check) {
      const emptyEnv = join(directory, 'empty.env'); await writeFile(emptyEnv, '');
      const env = testEnvironment(process.env, emptyEnv);
      env.NPEP_OPERATIONS_FIXTURES = join(directory, 'fixtures');
      if (report.preflight.postgresBin) env.NPEP_TEST_PG_BIN = report.preflight.postgresBin;
      await runStages(report.stages, async stage => {
        const ports = stage.evidence === 'browser' ? await browserPorts() : null;
        if (ports) { stage.testServices = {host: '127.0.0.1', ...ports}; await persist(); }
        stage.log = join(directory, `${stage.id}.stdout.log`); stage.errorLog = join(directory, `${stage.id}.stderr.log`);
        const out = openSync(stage.log, 'w'), error = openSync(stage.errorLog, 'w');
        console.log(`[Operations] ${stage.id} ...`);
        let child;
        try { child = spawnSync(stage.command, stage.args, {cwd: stage.cwd, env: childEnvironment(env, stage, directory, ports),
          windowsHide: true, stdio: ['ignore', out, error]}); }
        finally { closeSync(out); closeSync(error); }
        stage.exitCode = child.status;
        if (child.error || child.status !== 0) throw new Error(`${stage.id} failed (${child.error?.code || child.status}); inspect phase logs.`);
        const stdout = await readFile(stage.log, 'utf8');
        if (stage.evidence === 'tap') stage.result = readTapEvidence(stdout);
        else if (stage.evidence === 'trx') stage.result = readTrxEvidence(await readFile(join(directory, 'test-results/operations.trx'), 'utf8'));
        else if (stage.evidence === 'browser') stage.result = readBrowserEvidence(JSON.parse(await readFile(join(directory, 'browser.json'), 'utf8')));
        else if (stage.evidence === 'contract') {
          stage.result = JSON.parse(await readFile(join(directory, 'contract-result.json'), 'utf8'));
          assert.equal(stage.result.status, 'PASSED'); assert.equal(stage.result.cases.length, 4);
        } else if (stage.evidence === 'database') {
          stage.result = readDatabaseEvidence(stdout);
          stage.result.excluded = 'Native runner --skip-desktop excludes .NET transport scenarios; local desktop tests execute in their own stage.';
        } else if (stage.evidence === 'build') {
          const files = await readdir(join(directory, 'build/bin/NPEduTools.App'), {recursive: true});
          assert.ok(files.some(file => file.replaceAll('\\', '/').endsWith('/NPEduTools.App.dll')), 'Fresh WPF assembly missing');
          stage.result = {status: 'BUILT', applicationStarted: false};
        }
        console.log(`[Operations] ${stage.id} passed with execution evidence.`);
        return child.status;
      }, persist);
      const final = await sourceFingerprints(roots), finalSources = await inspectPairingSources(roots);
      for (const key of Object.keys(roots)) {
        assert.equal(final[key].sha256, fingerprints[key].sha256, `${key} source inputs changed during verification; rerun`);
        assert.equal(finalSources.sources[key].commit, report.preflight.sources.sources[key].commit, `${key} HEAD changed during verification; rerun`);
      }
      report.sourceStable = true;
    }
    report.status = settings.check ? 'CHECKED' : 'PASSED';
  } catch (error) {
    if (!report.preflight) report.preflight = {status: 'FAILED'};
    report.status = 'FAILED'; report.error = error.message; process.exitCode = 1;
    console.error(`[Operations] ${error.message}`);
  } finally { report.finishedAt = new Date().toISOString(); await persist(); }
  console.log(`[Operations] ${report.status}; device, production and deployment remain NOT_RUN.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(`[Operations] ${error.message}`); process.exitCode = 1; });
}
