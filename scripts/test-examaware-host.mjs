// Real ExamAware source-build integration test. No SDK or bridge API mocks.
// Usage: node scripts/test-examaware-host.mjs --source D:/.../ExamAware2 --playwright <playwright/package.json>
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import net from 'node:net';

const options = Object.fromEntries(Array.from({ length: (process.argv.length - 2) / 2 }, (_, i) =>
  [process.argv[2 + i * 2], process.argv[3 + i * 2]]));
assert.ok(options['--source'], 'Provide --source pointing to the built ExamAware2 checkout.');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = resolve(options['--source']);
const desktop = join(source, 'packages/desktop');
const require = createRequire(options['--playwright'] ? resolve(options['--playwright']) : import.meta.url);
const { _electron } = require('playwright');
const electronPath = createRequire(join(desktop, 'package.json'))('electron');
const version = JSON.parse(await readFile(join(desktop, 'package.json'), 'utf8')).version;
assert.equal(version, '1.5.2', 'This acceptance test is scoped to 1.5.2.');
const plugin = JSON.parse(await readFile(join(root, 'plugins/npedutools-examaware-bridge/package.json'), 'utf8'));
const packagePath = join(root, '.artifacts/examaware-bridge', `${plugin.name}-${plugin.version}.ea2x`);
await readFile(packagePath);
const fixture = await mkdtemp(join(tmpdir(), 'NPEduTools.ExamAware.Real.'));
const output = join(root, '.artifacts/examaware-real-host', new Date().toISOString().replace(/[:.]/g, '-'));
await mkdir(output, { recursive: true });
const wrapper = join(fixture, 'wrapper');
await mkdir(wrapper);
await writeFile(join(wrapper, 'package.json'), JSON.stringify({ name: 'npedutools-real-host-test', version, main: 'main.cjs' }));
await writeFile(join(wrapper, 'main.cjs'), `
const { app } = require('electron');
const fs = require('node:fs'), path = require('node:path');
const data = path.resolve(__dirname, '../userdata');
fs.mkdirSync(data, { recursive: true });
app.setPath('userData', data);
app.setPath('sessionData', data);
app.setAppLogsPath(path.join(data, 'logs'));
app.setAppPath(${JSON.stringify(desktop)});
globalThis.testGuards = { protocolRegistrations: 0, loginItemWrites: 0 };
app.setAsDefaultProtocolClient = () => { globalThis.testGuards.protocolRegistrations++; return false; };
app.setLoginItemSettings = () => { globalThis.testGuards.loginItemWrites++; throw new Error('Read-only fixture prohibits login-item changes'); };
import(require('node:url').pathToFileURL(${JSON.stringify(join(desktop, 'dist/main/index.js'))}).href);
`);
const pipe = `NPEduTools.Test.examaware.real.${randomUUID()}`;
const hostExe = join(root, 'src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.exe');
let host, ea, page;
const summary = {
  sourceCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: source, encoding: 'utf8', windowsHide: true }).trim(),
  hostVersion: version, pluginVersion: plugin.version, mode: 'isolated-source-build', checks: [],
  limits: ['Not an official packaged release', 'Native file/message dialogs adapted; permission dialog and installer are real',
    'Protocol registry writes blocked; second-instance command-line deeplinks are tested',
    'Login auto-start is read-only and refers to this development executable',
    'NPEduTools production ExamAware.exe path validation and launch are not exercised by this source-build test']
};
const check = name => { summary.checks.push(name); console.log(`PASS ${name}`); };
async function until(fn, label, timeout = 15000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    try { const result = await fn(); if (result) return result; } catch (error) { last = error; }
    await delay(100);
  }
  throw new Error(`Timed out: ${label}`, { cause: last });
}
async function request(capability) {
  return new Promise((res, rej) => {
    const body = Buffer.from(JSON.stringify({ version: 1, requestId: randomUUID(), capability }));
    const header = Buffer.alloc(4); header.writeInt32LE(body.length);
    const socket = net.connect('\\\\.\\pipe\\' + pipe);
    let buffer = Buffer.alloc(0);
    socket.setTimeout(4000, () => socket.destroy(new Error('Pipe timeout')));
    socket.on('error', rej);
    socket.on('connect', () => socket.write(Buffer.concat([header, body])));
    socket.on('data', data => {
      buffer = Buffer.concat([buffer, data]);
      if (buffer.length >= 4 && buffer.length >= 4 + buffer.readInt32LE()) {
        socket.destroy(); res(JSON.parse(buffer.subarray(4, 4 + buffer.readInt32LE()).toString()));
      }
    });
  });
}
const status = async () => (await request('examaware.status')).examAware;
async function state(expected) { return until(async () => (await status()).bridgeState === expected, expected); }
async function startHost() {
  host = spawn(hostExe, ['--pipe', pipe, '--data-dir', join(fixture, 'host')], { cwd: fixture, windowsHide: true, stdio: 'ignore' });
  await until(async () => (await request('host.ping')).outcome === 'Succeeded', 'Host ready');
}
async function stopHost() {
  if (host && host.exitCode === null && host.signalCode === null) {
    const exited = once(host, 'exit'); host.kill(); await exited;
  }
}
async function launch() {
  ea = await _electron.launch({ executablePath: electronPath, args: [wrapper], cwd: fixture, timeout: 15000 });
  page = await ea.firstWindow({ timeout: 10000 });
  page.setDefaultTimeout(8000);
  await page.getByText('设置', { exact: true }).waitFor();
  const data = await ea.evaluate(({ app }) => ({ path: app.getPath('userData'), version: app.getVersion(), packaged: app.isPackaged }));
  assert.equal(resolve(data.path), resolve(fixture, 'userdata'));
  assert.equal(data.version, version); assert.equal(data.packaged, false);
}
async function selectFile(file) {
  await ea.evaluate(({ dialog }, selected) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [selected] });
    dialog.showMessageBox = async (...args) => {
      globalThis.testMessages ??= []; globalThis.testMessages.push(args.at(-1));
      return { response: 0, checkboxChecked: false };
    };
  }, file);
}
async function card(label) {
  await page.getByText(label, { exact: true }).locator('..').getByRole('button').click();
}
async function pair() {
  await selectFile(join(fixture, 'pairing.json'));
  await card('连接 NPEduTools'); await state('Connected');
}
async function secondInstance(deepLink) {
  const child = spawn(electronPath, [wrapper, ...(deepLink ? [deepLink] : [])], { cwd: fixture, windowsHide: true, stdio: 'ignore' });
  const timer = setTimeout(() => child.kill(), 10000);
  try { const [code] = await once(child, 'exit'); assert.equal(code, 0); } finally { clearTimeout(timer); }
}
try {
  await startHost(); await launch(); check('isolated real ExamAware source build launched');
  await page.getByText('设置', { exact: true }).locator('..').getByRole('button').click();
  const settings = await until(() => ea.windows().find(p => p.url().includes('/settings')), 'settings window');
  settings.setDefaultTimeout(8000);
  await settings.getByText('插件', { exact: true }).first().click();
  await selectFile(packagePath);
  await settings.getByRole('button', { name: '插件包 (.ea2x)', exact: true }).click();
  await settings.getByText('确认插件权限', { exact: true }).waitFor();
  assert.ok((await settings.locator('body').innerText()).includes('network.tcp'));
  assert.ok(await settings.getByRole('button', { name: '允许并继续安装' }).isDisabled());
  await settings.getByText('我了解这些权限可能影响应用设置、集控连接及其他插件，并同意继续安装', { exact: true }).click();
  await settings.screenshot({ path: join(output, 'permissions.png') });
  await settings.getByRole('button', { name: '允许并继续安装' }).click();
  await until(async () => (await page.evaluate(() => window.api.plugins.list())).some(p => p.name === 'npedutools-examaware-bridge' && p.status === 'active'), 'plugin active');
  await page.getByText('连接 NPEduTools', { exact: true }).waitFor();
  check('real installer permission confirmation, isolated CJS load, renderer cards');
  const pairing = (await request('examaware.pairing.get')).examAwarePairing;
  await writeFile(join(fixture, 'pairing.json'), JSON.stringify(pairing));
  await pair();
  const connected = await status();
  assert.equal(connected.appVersion, version); assert.equal(connected.packaged, false);
  assert.equal(connected.autoStartRegistered, await ea.evaluate(({ app }) => app.getLoginItemSettings().openAtLogin));
  summary.connected = connected;
  await page.screenshot({ path: join(output, 'connected-home.png') });
  check('renderer pairing import, real SDK TCP, authenticated version and auto-start readback');
  await page.evaluate(() => window.api.plugins.reload('npedutools-examaware-bridge'));
  await state('Connected');
  await until(async () => await page.getByText('连接 NPEduTools', { exact: true }).count() === 1, 'one card after reload');
  check('plugin reload reconnects without duplicate cards');
  await page.evaluate(() => window.api.plugins.toggle('npedutools-examaware-bridge', false));
  await state('Disconnected'); assert.equal((await status()).autoStartRegistered, null);
  await until(async () => await page.getByText('连接 NPEduTools', { exact: true }).count() === 0, 'cards removed');
  await page.evaluate(() => window.api.plugins.toggle('npedutools-examaware-bridge', true));
  await state('Connected'); check('disable clears connection and status; re-enable restores pairing');
  await stopHost(); await startHost();
  assert.deepEqual((await request('examaware.pairing.get')).examAwarePairing, pairing);
  await state('Connected'); check('NPEduTools Host restart preserves pairing and reconnects');
  await ea.close(); ea = null; await state('Disconnected');
  await launch(); await state('Connected'); check('ExamAware restart restores plugin and pairing');
  await ea.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().find(w => w.webContents.getURL().includes('mainpage')).hide());
  await secondInstance();
  await until(() => ea.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().some(w => w.webContents.getURL().includes('mainpage') && w.isVisible())), 'main visible');
  check('real single-instance launch restores hidden main window');
  await secondInstance('examaware://settings/plugins');
  const pluginsPage = await until(() => ea.windows().find(p => p.url().includes('/settings/plugins')), 'plugins deeplink');
  await pluginsPage.getByRole('button', { name: '插件包 (.ea2x)', exact: true }).waitFor({ timeout: 8000 });
  await secondInstance('examaware://settings/basic');
  await until(() => ea.windows().some(p => p.url().includes('/settings/basic')), 'basic deeplink');
  check('second-instance plugin/basic settings deeplinks');
  await card('断开 NPEduTools'); await state('Disconnected');
  assert.equal((await status()).autoStartRegistered, null);
  await pair(); check('user disconnect clears pairing; fresh import reconnects');
  summary.guards = await ea.evaluate(() => globalThis.testGuards);
  assert.equal(summary.guards.loginItemWrites, 0);
  check('no auto-start write attempted; protocol association registration intercepted');
  summary.result = 'passed';
} catch (error) {
  summary.result = 'failed'; summary.error = error.stack;
  console.error(error); process.exitCode = 1;
  if (ea) {
    summary.plugins = await page.evaluate(() => window.api.plugins.list()).catch(() => []);
    await page.screenshot({ path: join(output, 'failure.png') }).catch(() => {});
  }
} finally {
  if (ea) await ea.close().catch(() => {});
  await stopHost();
  // Only delete the uniquely created test root; never an input checkout or user configuration.
  assert.ok(resolve(fixture).startsWith(resolve(tmpdir()) + '\\'));
  await rm(fixture, { recursive: true, force: true, maxRetries: 3 });
  await writeFile(join(output, 'summary.json'), JSON.stringify(summary, null, 2));
  console.log(`Report: ${output}`);
}
