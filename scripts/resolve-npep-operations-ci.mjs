import {readFile, appendFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

export function resolveOperationsCiSources(record, overrides) {
  if (!record || record.schemaVersion !== 1 || Object.keys(record).sort().join(',') !== 'backend_sha,schemaVersion,web_sha') {
    throw new Error('Unsupported operations CI source record.');
  }
  const sha = (value, name) => {
    if (typeof value !== 'string' || !/^[0-9a-f]{40}$/i.test(value)) throw new Error(`${name} must be a full commit SHA.`);
    return value.toLowerCase();
  };
  // Validate the checked-in defaults even when dispatch supplies overrides.
  const defaults = {web: sha(record.web_sha, 'Recorded web'), backend: sha(record.backend_sha, 'Recorded backend')};
  return {
    desktop: sha(overrides.desktop, 'Desktop'),
    web: sha(overrides.web || defaults.web, 'Web'),
    backend: sha(overrides.backend || defaults.backend, 'Backend'),
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const record = JSON.parse(await readFile(new URL('./npep-operations-ci.json', import.meta.url), 'utf8'));
  const sources = resolveOperationsCiSources(record, {desktop: process.env.DESKTOP_SHA, web: process.env.WEB_SHA, backend: process.env.BACKEND_SHA});
  if (process.env.GITHUB_ENV) await appendFile(process.env.GITHUB_ENV, `WEB_SHA=${sources.web}\nBACKEND_SHA=${sources.backend}\n`);
  console.log(JSON.stringify(sources));
}
