import {test} from 'node:test';
import assert from 'node:assert/strict';
import {repositories, resolveCurrentSources} from '../scripts/resolve-npep-current-ci.mjs';

const sha = digit => digit.repeat(40);
const request = {callerRepository: repositories.web, callerCommit: sha('a'), event: 'pull_request'};

test('each PR/push keeps its own head and resolves each sibling main exactly once', () => {
  for (const event of ['pull_request', 'push']) for (const caller of Object.keys(repositories)) {
    const calls = [];
    const result = resolveCurrentSources({...request, event, callerRepository: repositories[caller]}, repo => {
      calls.push(repo); return sha(calls.length === 1 ? 'b' : 'c');
    });
    assert.equal(result.sources[caller].commit, sha('a'));
    assert.equal(result.sources[caller].selection, 'CALLER');
    assert.equal(calls.length, 2);
    assert.equal(new Set(calls).size, 2);
    assert(!calls.includes(repositories[caller]));
  }
});

test('explicit sibling SHAs do not perform mutable lookups', () => {
  const result = resolveCurrentSources({...request, overrides: {desktop: sha('B'), backend: sha('c')}}, () => {
    throw new Error('Unexpected network lookup');
  });
  assert.equal(result.sources.desktop.commit, sha('b'));
  assert.equal(result.sources.web.commit, sha('a'));
  assert.equal(result.sources.backend.selection, 'EXPLICIT');
});

test('manual replay may select an older exact three-commit combination', () => {
  const result = resolveCurrentSources({...request, event: 'workflow_dispatch',
    overrides: {desktop: sha('b'), web: sha('c'), backend: sha('d')}}, () => { throw new Error('Unexpected lookup'); });
  assert.equal(result.trigger.commit, sha('a'));
  assert.equal(result.sources.web.commit, sha('c'));
});

test('a PR cannot substitute an unrelated successful source commit', () => {
  assert.throws(() => resolveCurrentSources({...request, overrides: {web: sha('b')}}, () => sha('c')), /triggering/);
});

test('reject branch names, abbreviated SHAs, malformed lookups and unsupported callers', () => {
  for (const value of ['main', 'a'.repeat(7), sha('a') + '\n', '-'.repeat(40), '']) {
    assert.throws(() => resolveCurrentSources({...request, callerCommit: value}, () => sha('b')), /40-character/);
  }
  assert.throws(() => resolveCurrentSources({...request, overrides: {backend: 'main'}}, () => sha('b')), /40-character/);
  assert.throws(() => resolveCurrentSources(request, () => 'main'), /40-character/);
  assert.throws(() => resolveCurrentSources({...request, callerRepository: 'other/NPClassworks'}), /three NPEP/);
  assert.throws(() => resolveCurrentSources({...request, event: 'pull_request_target'}), /trigger/);
  assert.throws(() => resolveCurrentSources({...request, overrides: {unknown: sha('b')}}), /Unknown/);
});

test('lookup failure stops resolution without falling back to stale pins', () => {
  assert.throws(() => resolveCurrentSources(request, () => { throw new Error('Network unavailable'); }), /Network unavailable/);
});
