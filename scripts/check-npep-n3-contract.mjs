import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

const [desktop, web, backend] = process.argv.slice(2).map(p => resolve(p));
const json = async (root, path) => JSON.parse(await readFile(resolve(root, path), 'utf8'));
const schema = await json(backend, 'domain/npep/runtime-control.schema.json');
assert.deepEqual(await json(backend, 'docs/npep-n3/runtime-control.schema.json'), schema, 'Backend documentation schema drift');
assert.deepEqual(await json(desktop, 'docs/npep/n3-wire.schema.json'), schema, 'Desktop/backend schema drift');
const examples = await json(backend, 'docs/npep-n3/examples.json');
assert.deepEqual(await json(desktop, 'docs/npep/n3-examples.json'), examples, 'Shared examples drift');
const {validateRuntime} = await import(pathToFileURL(resolve(backend, 'domain/npep/runtimeControl.js')));
const {runtimeCreateBody} = await import(pathToFileURL(resolve(web, 'src/utils/npepRuntimePresentation.js')));
for (const c of examples.cases) assert.equal(validateRuntime(c.definition, c.value), c.valid, c.name);
const id = '12345678-1234-4234-8234-123456789abc';
const request = {requestId: id, ...runtimeCreateBody({policy: {consentId: id, policyRevision: 2}, controlEpoch: id,
  status: {runtimeRevision: 3, modeRevision: 4, configurationRevision: 5}})};
assert.equal(validateRuntime('createRequest', request), true, 'Actual web request rejected by backend');
assert.equal(validateRuntime('createRequest', {...request, target: 'DAILY'}), true, 'Daily target rejected');
for (const extra of [{autostart: true}, {path: 'C:/example.exe'}, {deviceIds: [id]}, {target: 'SHUTDOWN'}])
  assert.equal(validateRuntime('createRequest', {...request, ...extra}), false, 'Authority extension must be rejected');
console.log(`N3 cross-repository contract passed: ${examples.cases.length} examples, actual web request, forbidden extensions, schema parity.`);
