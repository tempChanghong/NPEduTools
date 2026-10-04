import assert from 'node:assert/strict';
import {readFile, writeFile} from 'node:fs/promises';
import {resolve, join} from 'node:path';
import {pathToFileURL} from 'node:url';

export async function checkOperationsContract({desktop, web, backend, fixtures}) {
  const json = async path => JSON.parse(await readFile(path, 'utf8'));
  assert.deepEqual(await json(join(desktop, 'docs/npep/n1-wire.schema.json')),
    await json(join(backend, 'domain/npep/wire.schema.json')), 'N1 schema copies differ');
  const cases = await json(join(fixtures, 'desktop-cases.json'));
  assert.deepEqual(cases.map(item => item.name), ['unknown', 'daily', 'exam', 'incomplete'], 'Missing actual .NET cases');
  const {validate} = await import(pathToFileURL(join(backend, 'domain/npep/wire.js')));
  const {buildDeviceOverview, readOverviewQuery} = await import(pathToFileURL(join(backend, 'domain/npep/deviceOverview.js')));
  const {readDeviceOverview} = await import(pathToFileURL(join(web, 'src/utils/npepDeviceOverview.js')));
  const {createNpepOverviewDiagnostics} = await import(pathToFileURL(join(web, 'src/utils/npepDiagnostics.js')));
  const id = '12345678-1234-4234-8234-123456789abc', at = Date.parse('2026-10-04T10:00:00Z');
  const config = {serverInstanceId: id, deploymentEpoch: id};
  const binding = {id, isActive: true, administrativeClassId: id, npepBindingRevision: 1, name: 'SYNTHETIC_PRIVATE_NAME',
    administrativeClass: {id, type: 'ADMIN_CLASS', isActive: true, name: 'SYNTHETIC_PRIVATE_NAME', term: {status: 'ACTIVE', schoolId: id}}};
  for (const item of cases) {
    assert.equal(validate('status', item.status), true, `Backend rejects actual .NET status: ${item.name}`);
    const rows = [{id, ...config, schoolId: id, administrativeClassId: id, screenBindingId: id,
      bindingRevision: 1, deviceName: 'SYNTHETIC_PRIVATE_NAME', state: 'ACTIVE', credentialExpiresAt: new Date(at + 86400000),
      lastSeenAt: new Date(at), status: item.status, secretHash: 'SYNTHETIC_SECRET', credentialId: 'SYNTHETIC_SECRET'}];
    const overview = readDeviceOverview(buildDeviceOverview(rows, [binding], config, readOverviewQuery({}), at));
    assert.deepEqual(overview.items[0].issues, item.expectedIssues, `Mode-specific attention semantics: ${item.name}`);
    const report = createNpepOverviewDiagnostics({overview, devices: overview.items, schoolId: id, now: at, filter: 'ALL',
      appVersion: '1.2.0', backendUrl: 'https://api.example.test?token=SYNTHETIC_SECRET', hasMore: false});
    assert.equal(report.items[0].status.mode, item.status.mode);
    assert.equal(report.items[0].status.recording, item.status.recording);
    assert.equal(report.items[0].screenBindingId, item.desktop.connection.binding.screenBindingId);
    assert.equal(report.backendOrigin, item.desktop.connection.origin);
    for (const text of ['SYNTHETIC_SECRET', 'SYNTHETIC_PRIVATE_NAME', 'DO_NOT_EXPORT_PAIRING_OR_PRIVATE_CONTENT']) {
      assert.equal(JSON.stringify(report).includes(text), false, `Private data exported: ${item.name}`);
      assert.equal(JSON.stringify(item.desktop).includes(text), false);
    }
  }
  return {status: 'PASSED', cases: cases.map(item => item.name), source: 'executed-dotnet-status-and-diagnostic-builders',
    coverage: ['N1 schema parity', 'backend status validation and mode-aware overview', 'Vue overview reader', 'web diagnostic allow-list', 'binding and provider correlation']};
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [desktop, web, backend, fixtures, output] = process.argv.slice(2).map(value => resolve(value));
  const report = await checkOperationsContract({desktop, web, backend, fixtures});
  if (output) await writeFile(output, JSON.stringify(report, null, 2) + '\n');
  console.log(`Operations cross-repository contract passed: ${report.cases.length} actual .NET cases.`);
}
