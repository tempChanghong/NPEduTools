// Unified N4 acceptance, not a deployment or a real-device test.
import {spawnSync} from 'node:child_process';
import {createHash, randomUUID} from 'node:crypto';
import {openSync, closeSync} from 'node:fs';
import {readFile, writeFile, mkdir, realpath, access, readdir, rename} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {resolve, join} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {parseEnv} from 'node:util';

const desktopRoot = fileURLToPath(new URL('../', import.meta.url));
const backendTests = ['npepNoise', 'npepRuntime', 'npepHttpErrors', 'noiseScheduleRules', 'noiseScheduleSchema', 'npepNoiseSchedules', 'npepNoiseScheduleRuntime'];
const webTests = ['nativeNoise', 'nativeNoisePresentation', 'nativeNoiseTakeover', 'noiseMonitoringController', 'noiseScheduleLifecycle',
  'manualNoiseLifecycle', 'npepAdminClientFlows', 'schoolNoiseSchedule', 'noiseScheduleEditorFlows', 'nativeNoiseSchedule', 'noiseReportSources'];

export function createPlan(roots, {browser = false, database = false, resultDirectory = '.artifacts/n4-results'} = {}) {
  const tests = (id, repo, command, args, requested = true) => ({id, repo, command, args, cwd: roots[repo], requested, status: requested ? 'NOT_RUN' : 'NOT_REQUESTED'});
  return [
    tests('desktop', 'desktop', 'dotnet', ['test', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', '--artifacts-path', '.artifacts/n4-tests', '-m:1', '--verbosity', 'quiet',
      '--filter', 'FullyQualifiedName~Noise|FullyQualifiedName~SchoolClock|FullyQualifiedName~TestProcessPath|FullyQualifiedName~ProtocolTests|FullyQualifiedName~RemoteExam|FullyQualifiedName~ClassroomMode|FullyQualifiedName~Recording',
      '--logger', 'trx;LogFileName=n4-desktop.trx', '--results-directory', resultDirectory]),
    tests('transport', 'desktop', 'dotnet', ['test', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj', '--artifacts-path', '.artifacts/n4-tests', '-m:1', '--verbosity', 'quiet',
      '--logger', 'trx;LogFileName=n4-transport.trx', '--results-directory', resultDirectory]),
    tests('backend', 'backend', process.execPath, ['--test', '--test-concurrency=1', ...backendTests.map(name => `tests/${name}.test.js`)]),
    tests('web', 'web', process.execPath, ['--test', '--test-concurrency=1', ...webTests.map(name => `tests/${name}.test.js`)]),
    tests('browser-manual', 'web', process.execPath, ['scripts/test-native-noise-browser.mjs'], browser),
    tests('browser-schedule', 'web', process.execPath, ['scripts/test-noise-schedules-browser.mjs'], browser),
    tests('database', 'backend', process.execPath, ['scripts/run-native-npep-tests.js', '--desktop-root', roots.desktop], database),
  ];
}

export function testEnvironment(source, emptyEnv) {
  const allowed = new Set(['path', 'pathext', 'systemroot', 'windir', 'comspec', 'temp', 'tmp', 'tmpdir', 'userprofile', 'appdata', 'localappdata',
    'programfiles', 'programfiles(x86)', 'programdata', 'home', 'homedrive', 'homepath', 'lang', 'lc_all', 'term', 'username',
    'dotnet_root', 'dotnet_cli_home', 'nuget_packages', 'npep_test_pg_bin']);
  return {...Object.fromEntries(Object.entries(source).filter(([key]) => allowed.has(key.toLowerCase()))), NODE_ENV: 'test',
    DOTENV_CONFIG_PATH: emptyEnv, DOTENV_CONFIG_OVERRIDE: 'false', DOTNET_NOLOGO: 'true', RUN_DATABASE_TESTS: 'false'};
}

export async function verifyContractGroups(groups) {
  const checked = [];
  for (const paths of groups) {
    const hashes = await Promise.all(paths.map(async path => createHash('sha256').update(await readFile(path)).digest('hex')));
    if (new Set(hashes).size !== 1) throw new Error(`Cross-repository contract mismatch: ${paths.join(', ')}`);
    checked.push({paths, sha256: hashes[0]});
  }
  return checked;
}

// A nonzero exit or a spawn error stops the remaining phases. Persist RUNNING
// before each child, so interrupted runs can never leave a false PASSED record.
export async function runStages(stages, execute, persist = async () => {}) {
  for (const stage of stages.filter(item => item.requested)) {
    stage.status = 'RUNNING'; stage.startedAt = new Date().toISOString(); await persist();
    try {
      const exitCode = await execute(stage);
      if (exitCode !== 0) throw new Error(`${stage.id} exited with ${exitCode ?? 'a signal'}`);
      stage.exitCode = exitCode; stage.status = 'PASSED';
    } catch (error) {
      stage.status = 'FAILED'; stage.error = error.message; stage.finishedAt = new Date().toISOString(); await persist();
      throw error;
    }
    stage.finishedAt = new Date().toISOString(); await persist();
  }
}

function options(args) {
  const result = {browser: false, database: false, check: false};
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (['--browser', '--database', '--check'].includes(arg)) result[arg.slice(2)] = true;
    else if (['--web-root', '--backend-root', '--powershell-version'].includes(arg) && args[i + 1] && !args[i + 1].startsWith('--')) {
      result[arg.slice(2)] = args[++i];
    } else throw new Error(`Unknown or incomplete option: ${arg}`);
  }
  return result;
}

function capture(command, args, cwd) {
  const value = spawnSync(command, args, {cwd, encoding: 'utf8', windowsHide: true});
  if (value.error || value.status !== 0) throw new Error(`${command} prerequisite check failed`);
  return value.stdout.trim();
}

async function preflight(roots, plan, settings) {
  if (process.platform !== 'win32') throw new Error('N4 desktop acceptance requires Windows for DPAPI and named pipes.');
  if (typeof parseEnv !== 'function') throw new Error('Node runtime does not support the current native development entry.');
  if (new Set(Object.values(roots)).size !== 3) throw new Error('Desktop, web and backend must be three distinct checkouts.');
  const commits = {};
  for (const [name, root] of Object.entries(roots)) {
    const gitRoot = await realpath(capture('git', ['rev-parse', '--show-toplevel'], root));
    if (gitRoot !== root) throw new Error(`${name} must be a repository root.`);
    const state = capture('git', ['status', '--porcelain', '--untracked-files=normal'], root);
    commits[name] = {root, commit: capture('git', ['rev-parse', 'HEAD'], root), dirty: !!state};
  }
  const webPackage = JSON.parse(await readFile(join(roots.web, 'package.json'), 'utf8'));
  const backendPackage = JSON.parse(await readFile(join(roots.backend, 'package.json'), 'utf8'));
  if (webPackage.name !== 'classworks' || backendPackage.name !== 'ClassworksKV') throw new Error('The web/backend paths point to the wrong products.');
  for (const [root, files] of [[roots.desktop, ['global.json', 'src/NPEduTools.App/NPEduTools.App.csproj', 'tests/NPEduTools.Tests/NPEduTools.Tests.csproj', 'tests/NPEduTools.Npep.Tests/NPEduTools.Npep.Tests.csproj']],
    [roots.web, ['node_modules/vue/package.json', 'node_modules/vite/bin/vite.js', 'node_modules/@playwright/test/package.json', ...webTests.map(name => `tests/${name}.test.js`)]],
    [roots.backend, ['node_modules/ajv/package.json', 'node_modules/prisma/build/index.js', 'node_modules/pg/package.json', 'generated/prisma/client.ts', ...backendTests.map(name => `tests/${name}.test.js`)] ]])
    for (const path of files) await access(join(root, path));
  const at = (repo, path) => join(roots[repo], path);
  const contracts = await verifyContractGroups([
    [at('desktop', 'docs/npep/noise.schema.json'), at('backend', 'domain/npep/noise.schema.json')],
    [at('desktop', 'docs/npep/noise-schedule-cases.json'), at('backend', 'tests/fixtures/noise-schedule-cases.json'), at('web', 'tests/fixtures/noise-schedule-cases.json')],
    [at('backend', 'domain/npep/noiseScheduleRules.js'), at('web', 'src/utils/schoolNoiseSchedule.js')],
    [at('backend', 'tests/helpers/noiseScheduleCases.js'), at('web', 'tests/helpers/noiseScheduleCases.js')],
    [at('desktop', 'docs/npep/noise-schedule-policy.schema.json'), at('backend', 'domain/npep/noise-schedule-policy.schema.json')],
    [at('desktop', 'docs/npep/noise-schedule-wire.schema.json'), at('backend', 'domain/npep/noise-schedule-wire.schema.json')],
    [at('desktop', 'docs/npep/noise-schedule-wire-cases.json'), at('backend', 'domain/npep/noise-schedule-wire-cases.json')],
  ]);
  const migrationNames = ['20260930000000_npep_noise', '20261001000000_npep_noise_schedules', '20261001010000_npep_noise_schedule_runtime'];
  for (const name of migrationNames) await access(at('backend', `prisma/migrations/${name}/migration.sql`));
  const runtime = {node: process.version, dotnet: capture('dotnet', ['--version'], roots.desktop), powershell: settings['powershell-version'] ?? null};
  if (plan.some(stage => stage.requested && stage.id.startsWith('browser-'))) {
    const require = createRequire(at('web', 'package.json'));
    await access(require('@playwright/test').chromium.executablePath());
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
    if (!postgresBin) throw new Error('Set NPEP_TEST_PG_BIN to an installed native PostgreSQL bin directory.');
    for (const binary of ['initdb.exe', 'pg_ctl.exe', 'pg_dump.exe', 'pg_restore.exe']) await access(join(postgresBin, binary));
  }
  return {commits, runtime, contracts, migrations: {present: migrationNames, developmentApplied: 'NOT_CHECKED', productionApplied: 'NOT_CHECKED'}, postgresBin};
}

async function main() {
  const settings = options(process.argv.slice(2));
  const directory = join(desktopRoot, '.artifacts/npep-n4', `${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`);
  await mkdir(directory, {recursive: true});
  const report = {schemaVersion: 1, startedAt: new Date().toISOString(), mode: settings.check ? 'CHECK_ONLY' : 'TEST', status: 'RUNNING',
    releaseReady: false, manualAcceptance: 'NOT_RUN', productionAcceptance: 'NOT_RUN', deployment: 'NOT_RUN', stages: []};
  const persist = async () => {
    const temporary = join(directory, 'result.tmp.json');
    await writeFile(temporary, JSON.stringify(report, null, 2) + '\n');
    await rename(temporary, join(directory, 'result.json'));
  };
  console.log(`[N4] Result directory: ${directory}`); await persist();
  try {
    const roots = {desktop: await realpath(desktopRoot), web: await realpath(resolve(settings['web-root'] || join(desktopRoot, '../NPClassworks'))),
      backend: await realpath(resolve(settings['backend-root'] || join(desktopRoot, '../NPClassworksKV')))};
    report.stages = createPlan(roots, {...settings, resultDirectory: join(directory, 'test-results')}); await persist();
    report.preflight = {status: 'PASSED', ...await preflight(roots, report.stages, settings)}; await persist();
    console.log('[N4] Prerequisites and seven shared contract groups passed. Existing databases were not accessed.');
    if (!settings.check) {
      const emptyEnv = join(directory, 'empty.env'); await writeFile(emptyEnv, '');
      const env = testEnvironment(process.env, emptyEnv);
      if (report.preflight.postgresBin) env.NPEP_TEST_PG_BIN = report.preflight.postgresBin;
      await runStages(report.stages, stage => {
        stage.log = join(directory, `${stage.id}.log`);
        const fd = openSync(stage.log, 'w');
        console.log(`[N4] Running ${stage.id} ...`);
        try {
          const child = spawnSync(stage.command, stage.args, {cwd: stage.cwd, env, windowsHide: true, stdio: ['ignore', fd, fd]});
          if (child.error) throw new Error(`${stage.id} could not start: ${child.error.code}`);
          stage.exitCode = child.status;
          if (child.status === 0) console.log(`[N4] ${stage.id} passed.`);
          return child.status;
        } finally { closeSync(fd); }
      }, persist);
    }
    report.status = settings.check ? 'CHECKED' : 'PASSED';
  } catch (error) {
    if (!report.preflight) report.preflight = {status: 'FAILED', error: error.message};
    report.status = 'FAILED'; report.error = error.message; process.exitCode = 1;
    console.error(`[N4] Failed: ${error.message}. Inspect result.json and the phase log.`);
  } finally {
    report.finishedAt = new Date().toISOString(); await persist();
  }
  console.log(`[N4] ${report.status}. Manual/device/production acceptance and deployment remain NOT_RUN.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href)
  main().catch(error => { console.error(`[N4] ${error.message}`); process.exitCode = 1; });
