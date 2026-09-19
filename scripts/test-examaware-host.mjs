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
const e4 = options['--e4'] === 'true';
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
if (${e4}) {
  // Exercise the real SDK at the native login-item boundary without modifying Windows registry.
  const loginFile = path.resolve(__dirname, '../login-item-fixture.json');
  globalThis.testLoginItem = { mode: 'normal', writes: [], readsSinceWrite: 0 };
  app.getLoginItemSettings = () => {
    const state = globalThis.testLoginItem;
    state.readsSinceWrite++;
    if (state.mode === 'readbackFailure' && state.readsSinceWrite >= 2) throw new Error('Isolated native readback failure');
    return { openAtLogin: fs.existsSync(loginFile) ? JSON.parse(fs.readFileSync(loginFile, 'utf8')).enabled : false };
  };
  app.setLoginItemSettings = options => {
    const state = globalThis.testLoginItem;
    if (Object.keys(options).join() !== 'openAtLogin' || typeof options.openAtLogin !== 'boolean') throw new Error('Unexpected login-item options');
    state.writes.push(options.openAtLogin); state.readsSinceWrite = 0;
    if (state.mode === 'writeFailure') throw new Error('Isolated native write failure');
    if (state.mode !== 'mismatch') fs.writeFileSync(loginFile, JSON.stringify({ enabled: options.openAtLogin }));
  };
}
import(require('node:url').pathToFileURL(${JSON.stringify(join(desktop, 'dist/main/index.js'))}).href);
`);
const pipe = `NPEduTools.Test.examaware.real.${randomUUID()}`;
const e3 = options['--e3'] === 'true' || e4;
const hostExe = e3 ? join(root, 'tests/NPEduTools.ExamAware.TestHost/bin/Release/net10.0/NPEduTools.ExamAware.TestHost.exe') : join(root, 'src/NPEduTools.Host/bin/Release/net10.0/NPEduTools.Host.exe');
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
async function request(capability, extra = {}) {
  return new Promise((res, rej) => {
    const body = Buffer.from(JSON.stringify({ version: 1, requestId: randomUUID(), capability, ...extra }));
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
  host = spawn(hostExe, ['--pipe', pipe, '--data-dir', join(fixture, 'host'), ...(e3 ? ['--executable', electronPath] : [])], { cwd: fixture, windowsHide: true, stdio: 'ignore' });
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
  await ea.evaluate(() => { globalThis.bridgeDiagnostics = []; const warn = console.warn;
    console.warn = (...args) => { if (String(args[0]).includes('[NPEduTools bridge]')) globalThis.bridgeDiagnostics.push(args.map(String).join(' ')); warn(...args); }; });
  const data = await ea.evaluate(({ app }) => ({ path: app.getPath('userData'), version: app.getVersion(), packaged: app.isPackaged }));
  assert.equal(resolve(data.path), resolve(fixture, 'userdata'));
  assert.equal(data.version, version); assert.equal(data.packaged, false);
  const ids = { launched: ea.process().pid, main: await ea.evaluate(() => process.pid) };
  summary.processes ??= []; summary.processes.push(ids);
}
// Cleanup only our isolated Electron fixture, including intentional policy/cancel refusals.
// This is never used by NPEduTools or to determine whether a quit command succeeded.
async function disposeFixture() {
  if (!ea) return;
  const closing = ea.waitForEvent('close', { timeout: 10000 });
  await ea.evaluate(({ app }) => { setTimeout(() => app.exit(0), 100); }).catch(() => {});
  await closing.catch(() => {});
  ea = null;
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
  await page.evaluate(() => window.api.windows.openSettings('plugins'));
  await selectFile(packagePath);
  await settings.getByRole('button', { name: '插件包 (.ea2x)', exact: true }).click();
  await settings.getByText('确认插件权限', { exact: true }).waitFor();
  assert.ok((await settings.locator('body').innerText()).includes('network.tcp'));
  if (e4) {
    assert.ok(plugin.examaware.permissions.includes('app.configure'));
    // 1.5.2 only lists its sensitive allowlist here; app.configure is not in that list.
    summary.autoStartPermissionListedInDialog = (await settings.locator('body').innerText()).includes('app.configure');
  }
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
  if (e3) {
    summary.limits.push('Quit uses test-only source-path adapter; process observation remains production code');
    await request('examaware.pairing.reset'); await state('Disconnected');
    assert.notEqual((await request('examaware.pairing.get')).examAwarePairing.key, pairing.key);
    await writeFile(join(fixture, 'pairing.json'), JSON.stringify((await request('examaware.pairing.get')).examAwarePairing));
    await pair(); check('pairing revocation invalidates old key; fresh import restores connection');
    assert.equal((await request('examaware.config.set', { executablePath: electronPath, expectedRevision: (await status()).revision })).outcome, 'Succeeded');
    if (e4) {
      summary.limits.push('E4 uses real SDK with a file-backed native login-item adapter; no Windows Run/StartupApproved writes or real login test');
      await ea.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().filter(w => w.webContents.getURL().includes('/settings')).forEach(w => w.close()));
      assert.equal((await status()).canSetAutoStart, true);
      const revision = (await status()).revision;
      for (const enabled of [true, false]) {
        const reply = await request('examaware.autostart.set', { autoStartEnabled: enabled, expectedRevision: revision });
        assert.equal(reply.outcome, 'Accepted');
        await until(async () => (await status()).autoStartChange?.state === 'Succeeded', 'auto-start matching readback');
        assert.equal((await status()).autoStartRegistered, enabled);
        assert.equal((await status()).autoStartChange.registered, enabled);
        const writes = await ea.evaluate(() => globalThis.testLoginItem.writes.length);
        assert.equal((await request('examaware.autostart.set', { requestId: reply.requestId, autoStartEnabled: enabled, expectedRevision: revision })).errorCode, 'AlreadyAccepted');
        assert.equal(await ea.evaluate(() => globalThis.testLoginItem.writes.length), writes);
        check(enabled ? 'real SDK enables login item and independently reads true; duplicate write rejected' : 'real SDK disables login item and reads false as success; basic settings closed');
      }
      for (const [mode, expected] of [['mismatch', 'Mismatch'], ['writeFailure', 'Failed'], ['readbackFailure', 'Unconfirmed']]) {
        await ea.evaluate((_, value) => { globalThis.testLoginItem.mode = value; }, mode);
        assert.equal((await request('examaware.autostart.set', { autoStartEnabled: true, expectedRevision: revision })).outcome, 'Accepted');
        await until(async () => (await status()).autoStartChange?.state === expected, mode);
        check(`native boundary ${mode} produces ${expected}, never optimistic success`);
        await ea.evaluate(() => { globalThis.testLoginItem.mode = 'normal'; });
      }
      await until(async () => (await status()).autoStartRegistered === true, 'readback recovers after uncertain write');
      await ea.close(); ea = null; await launch(); await state('Connected');
      assert.equal((await status()).autoStartRegistered, true);
      assert.deepEqual(await ea.evaluate(() => globalThis.testLoginItem.writes), []);
      check('fixture registration is read after restart without replaying any write');
      await writeFile(join(fixture, 'login-item-fixture.json'), JSON.stringify({ enabled: false }));
      await until(async () => (await status()).autoStartRegistered === false, 'external registration change');
      check('heartbeat reflects external registration change; history is separate from live state');
      summary.autoStartNativeAdapter = true;
    }
    for (const hidden of [false, true]) {
      if (hidden) await ea.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().forEach(w => w.hide()));
      const mainPid = await ea.evaluate(() => process.pid);
      const quit = await request('examaware.quit'); assert.equal(quit.outcome, 'Accepted');
      assert.equal(quit.examAware.quit.processId, mainPid);
      await until(async () => (await status()).quit?.state === 'Exited', 'OS-confirmed quit');
      assert.equal((await request('examaware.quit', { requestId: quit.requestId })).errorCode, 'AlreadyAccepted');
      ea = null;
      check(hidden ? 'tray-hidden real SDK quit confirmed by retained process handle' : 'normal real SDK quit confirmed by retained process handle');
      await launch(); await state('Connected');
    }
    await ea.close(); ea = null;
    await writeFile(join(fixture, 'userdata/managed-control.json'), JSON.stringify({ 'control.preventQuit': true }));
    await launch(); await state('Connected');
    assert.equal((await request('examaware.quit')).outcome, 'Accepted');
    await until(async () => (await status()).quit?.state === 'Denied', 'official control policy refusal');
    assert.equal((await status()).bridgeState, 'Connected');
    check('official SDK rejects quit under isolated school-control policy; process and bridge remain alive');
    // Test-only teardown of a fixture deliberately configured to forbid quitting; never a product fallback.
    await disposeFixture();
    await rm(join(fixture, 'userdata/managed-control.json'));
    await launch(); await state('Connected');
    const examFile = join(fixture, 'exam.json');
    await writeFile(examFile, JSON.stringify({ examName: 'E3 isolated test', message: 'Fixture only', examInfos: [
      { name: 'E3 test subject', start: '2026-09-20T09:00:00', end: '2026-09-20T10:00:00', alertTime: 5, materials: [] }
    ] }));
    for (const choice of [2, 0, 1]) {
      await page.evaluate(file => window.api.windows.openEditor(file), examFile);
      const editor = await until(() => ea.windows().find(p => p.url().includes('/editor')), 'editor window');
      editor.setDefaultTimeout(8000);
      await editor.getByText('E3 test subject', { exact: true }).first().waitFor();
      const edited = `E3 unsaved choice ${choice}`;
      const input = editor.getByPlaceholder('请输入考试相关信息...');
      await input.fill(edited); await input.press('Tab');
      await until(async () => (await editor.locator('body').innerText()).includes(' •'), 'modified editor indicator');
      const originalFile = await readFile(examFile, 'utf8');
      await ea.evaluate(({ dialog }, response) => {
        globalThis.unsavedDialogs = [];
        dialog.showMessageBox = async (...args) => {
          const options = args.at(-1);
          globalThis.unsavedDialogs.push(options);
          return { response, checkboxChecked: false };
        };
      }, choice);
      // Close the editor while its IPC is still available, before app.quit cleanup.
      await ea.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().find(w => w.webContents.getURL().includes('/editor')).close());
      if (choice === 2) {
        await until(async () => (await ea.evaluate(() => globalThis.unsavedDialogs)).length > 0, 'unsaved native confirmation');
        assert.equal(editor.isClosed(), false);
        assert.equal(await input.inputValue(), edited);
        assert.equal(await readFile(examFile, 'utf8'), originalFile);
        const dialogs = await ea.evaluate(() => globalThis.unsavedDialogs);
        assert.ok(dialogs.length > 0); assert.deepEqual(dialogs[0].buttons, ['保存', '不保存', '取消']);
        check('native editor cancel keeps data, process and bridge alive');
        assert.equal((await status()).bridgeState, 'Connected');
        // Known 1.5.2 limitation: app.quit removes dialog IPC before editor confirmation.
        const errors = []; editor.on('pageerror', error => errors.push(error.message));
        editor.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
        assert.equal((await request('examaware.quit')).outcome, 'Accepted');
        await until(async () => (await status()).quit?.state === 'Unconfirmed', 'open editor cannot complete upstream quit');
        assert.equal(editor.isClosed(), false);
        assert.equal(await input.inputValue(), edited);
        assert.equal(await readFile(examFile, 'utf8'), originalFile);
        assert.equal((await ea.evaluate(() => globalThis.unsavedDialogs)).length, dialogs.length);
        summary.editorQuitLimitation = { errors, state: (await status()).quit.state, bridgeState: (await status()).bridgeState };
        await editor.screenshot({ path: join(output, 'unsaved-cancel.png') });
        check('open-editor upstream quit limitation leaves data in memory; Host reports Unconfirmed, never Exited');
        await disposeFixture();
      } else {
        await until(() => editor.isClosed(), 'editor decision closes editor');
        if (choice === 0) assert.equal(JSON.parse(await readFile(examFile, 'utf8')).message, edited);
        else assert.equal(await readFile(examFile, 'utf8'), originalFile);
        assert.equal((await request('examaware.quit')).outcome, 'Accepted');
        await until(async () => (await status()).quit?.state === 'Exited', 'quit after closing editor');
        ea = null;
        check(choice === 0 ? 'save and close editor first, then normal quit succeeds' : 'discard and close editor first, then normal quit succeeds');
      }
      await launch(); await state('Connected');
    }
    await page.evaluate(file => window.api.player.start({ kind: 'file', path: file }), examFile);
    const player = await until(() => ea.windows().find(p => p.url().includes('/player')), 'player window');
    await player.getByText('E3 test subject', { exact: true }).first().waitFor({ timeout: 10000 });
    assert.equal((await request('examaware.quit')).outcome, 'Accepted');
    await until(async () => (await status()).quit?.state === 'Exited', 'player normal quit');
    ea = null; check('active exam player exits through official SDK with OS confirmation');
  }
  summary.result = 'passed';
} catch (error) {
  summary.result = 'failed'; summary.error = error.stack;
  summary.lastStatus = await status().catch(() => null);
  console.error(error); process.exitCode = 1;
  if (ea) {
    summary.bridgeDiagnostics = await ea.evaluate(() => globalThis.bridgeDiagnostics).catch(() => null);
    summary.plugins = await page.evaluate(() => window.api.plugins.list()).catch(() => []);
    await page.screenshot({ path: join(output, 'failure.png') }).catch(() => {});
  }
} finally {
  await disposeFixture();
  await stopHost();
  // Only delete the uniquely created test root; never an input checkout or user configuration.
  assert.ok(resolve(fixture).startsWith(resolve(tmpdir()) + '\\'));
  await rm(fixture, { recursive: true, force: true, maxRetries: 3 });
  await writeFile(join(output, 'summary.json'), JSON.stringify(summary, null, 2));
  console.log(`Report: ${output}`);
}
