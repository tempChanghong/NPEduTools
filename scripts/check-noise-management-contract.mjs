import assert from 'node:assert/strict';
import {readFile, readdir} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL, fileURLToPath} from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const backend = resolve(process.argv[2] || resolve(root, '../NPClassworksKV'));
const snapshots = process.argv[3];
const cases = JSON.parse(await readFile(resolve(root, 'docs/npep/noise-management-wire-cases.json'), 'utf8'));
const {validateNoiseManagement} = await import(pathToFileURL(resolve(backend, 'domain/npep/noiseManagement.js')).href);
assert.ok(snapshots, 'Run the C# management suites with a fresh fixture directory first.');
assert.equal((await readdir(snapshots)).filter(f => f.endsWith('.json')).length, cases.length, 'Missing or stale desktop parser evidence');
for (const c of cases) {
  const actual = JSON.parse(await readFile(resolve(snapshots, c.name + '.json'), 'utf8'));
  assert.equal(actual.name, c.name);
  assert.equal(actual.definition, c.definition);
  assert.deepEqual(actual.value, c.value);
  assert.equal(actual.acceptedByDesktop, c.expected, `Desktop: ${c.name}`);
  assert.equal(Boolean(validateNoiseManagement(c.definition, c.value)), actual.acceptedByDesktop, `KV/Desktop differ: ${c.name}`);
}
console.log(`${cases.length} noise.management 0.8 cases agree across the real desktop parser and KV validator.`);
