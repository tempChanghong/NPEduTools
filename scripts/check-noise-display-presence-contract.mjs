import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL, fileURLToPath} from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const backend = resolve(process.argv[2] || resolve(root, '../NPClassworksKV'));
const directory = process.argv[3];
assert.ok(directory, 'Run the desktop parser against a fresh fixture directory first.');
const examples = JSON.parse(await readFile(resolve(root, 'docs/npep/noise-display-presence-wire-cases.json'), 'utf8'));
assert.deepEqual(examples, JSON.parse(await readFile(resolve(backend, 'domain/npep/noise-display-presence-wire-cases.json'), 'utf8')));
const {validateNoiseDisplayPresence} = await import(pathToFileURL(resolve(backend, 'domain/npep/noiseDisplayPresence.js')).href);
const results = JSON.parse(await readFile(resolve(directory, 'parser-results.json'), 'utf8'));
assert.equal(results.length, 20, 'Missing fresh desktop parser evidence');
for (const actual of results) {
  assert.equal(Boolean(validateNoiseDisplayPresence(actual.definition, actual.value)), actual.acceptedByDesktop, actual.name);
}
console.log(`${results.length} presence 0.9 requests agree between actual C# and KV validators; shared reply also passed C# parsing.`);
