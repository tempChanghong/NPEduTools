import {execFileSync} from 'node:child_process';
import {appendFileSync, mkdirSync, writeFileSync} from 'node:fs';
import {dirname, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

export const repositories = Object.freeze({
  desktop: 'tempChanghong/NPEduTools',
  web: 'tempChanghong/NPClassworks',
  backend: 'tempChanghong/NPClassworksKV',
});

function fullSha(value) {
  if (typeof value !== 'string' || !/^[a-f\d]{40}$/i.test(value)) {
    throw new Error('Source references must be full 40-character commit SHAs.');
  }
  return value.toLowerCase();
}

export function lookupMain(repository) {
  if (!Object.values(repositories).includes(repository)) throw new Error('Unsupported repository.');
  const output = execFileSync('git', ['ls-remote', '--exit-code',
    `https://github.com/${repository}.git`, 'refs/heads/main'],
  {encoding: 'utf8', timeout: 60000, windowsHide: true}).trim();
  const match = /^([a-f\d]{40})\s+refs\/heads\/main$/i.exec(output);
  if (!match) throw new Error(`Unable to resolve one main commit for ${repository}.`);
  return fullSha(match[1]);
}

export function resolveCurrentSources({callerRepository, callerCommit, event, overrides = {}}, lookup = lookupMain) {
  const caller = Object.keys(repositories).find(key => repositories[key].toLowerCase() === callerRepository?.toLowerCase());
  if (!caller) throw new Error('Only the three NPEP repositories can call this workflow.');
  callerCommit = fullSha(callerCommit);
  if (!['pull_request', 'push', 'workflow_dispatch'].includes(event)) throw new Error('Unsupported trigger.');
  for (const key of Object.keys(overrides)) {
    if (!Object.hasOwn(repositories, key)) throw new Error('Unknown source override.');
    if (overrides[key]) fullSha(overrides[key]);
  }
  // A PR/push must test the triggering source, even if a caller supplies overrides.
  if (event !== 'workflow_dispatch' && overrides[caller] && fullSha(overrides[caller]) !== callerCommit) {
    throw new Error('Cannot replace the triggering PR/push commit.');
  }
  const sources = {};
  for (const [key, repository] of Object.entries(repositories)) {
    const explicit = overrides[key];
    const selection = explicit ? 'EXPLICIT' : key === caller ? 'CALLER' : 'MAIN';
    const commit = fullSha(explicit || (key === caller ? callerCommit : lookup(repository)));
    sources[key] = {repository, commit, selection};
  }
  return {schemaVersion: 1, category: 'CURRENT', resolvedAt: new Date().toISOString(),
    trigger: {repository: repositories[caller], commit: callerCommit, event}, sources};
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const allowed = ['--caller-repository', '--caller-commit', '--event', '--desktop-sha', '--web-sha', '--backend-sha', '--output'];
  const options = {};
  for (let index = 2; index < process.argv.length; index += 2) {
    const key = process.argv[index], value = process.argv[index + 1];
    if (!allowed.includes(key) || !value || value.startsWith('--') || key in options) throw new Error('Invalid resolver option.');
    options[key] = value;
  }
  if (!options['--output']) throw new Error('A source manifest output path is required.');
  const result = resolveCurrentSources({callerRepository: options['--caller-repository'],
    callerCommit: options['--caller-commit'], event: options['--event'],
    overrides: Object.fromEntries(['desktop', 'web', 'backend'].map(key => [key, options[`--${key}-sha`]]))});
  mkdirSync(dirname(resolve(options['--output'])), {recursive: true});
  writeFileSync(options['--output'], JSON.stringify(result, null, 2) + '\n');
  for (const [key, source] of Object.entries(result.sources)) {
    console.log(`${key}=${source.commit} (${source.selection})`);
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${key}_sha=${source.commit}\n`);
  }
}
