import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// Offline check: first-party Markdown links only; never fetch remote sites.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const submodules = [...readFileSync(resolve(root, '.gitmodules'), 'utf8')
  .matchAll(/^\s*path\s*=\s*(.+)$/gm)].map(match => match[1].trim());
const files = [...new Set(execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'],
  { cwd: root, encoding: 'utf8' }).split('\0'))]
  .filter(path => path.endsWith('.md') && existsSync(resolve(root, path))
    && !path.startsWith('third-party/') && !submodules.some(dir => path.startsWith(`${dir}/`)));
let checked = 0;
const skipped = new Set();
const localEvidence = [];
const failures = [];

function withoutCode(text) {
  let fence;
  return text.split('\n').map(line => {
    const marker = line.match(/^\s*(`{3,}|~{3,})/);
    if (marker) {
      if (!fence) fence = marker[1];
      else if (marker[1][0] === fence[0] && marker[1].length >= fence.length) fence = undefined;
      return '';
    }
    return fence ? '' : line.replace(/(`+)[^`]*?\1/g, '');
  }).join('\n');
}

function check(file, target, line) {
  if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(target)) return;
  if (/^\/?[a-z]:[\/\\]/i.test(target)) { localEvidence.push(`${file}:${line}: ${target}`); return; }
  const hash = target.indexOf('#');
  const pathname = (hash < 0 ? target : target.slice(0, hash)).split('?')[0];
  if (!pathname) return; // Heading anchors are checked during editing; this checks file paths.
  let decoded;
  try { decoded = decodeURIComponent(pathname); }
  catch { failures.push(`${file}:${line}: invalid URL encoding: ${target}`); return; }
  const fullPath = resolve(dirname(resolve(root, file)), decoded);
  const repoPath = relative(root, fullPath).split(sep).join('/');
  // Historical evidence refers to ignored build outputs or another developer checkout.
  // Report these separately; their absence must not fail a clean clone or imply verification.
  if (repoPath.startsWith('../') || /(?:^|\/)\.artifacts(?:\/|$)/.test(repoPath)
    || /(?:^|\/)TestResults(?:\/|$)/.test(repoPath)) {
    localEvidence.push(`${file}:${line}: ${target}`); return;
  }
  const module = submodules.find(dir => repoPath === dir || repoPath.startsWith(`${dir}/`));
  if (module) { skipped.add(module); return; }
  checked++;
  if (!existsSync(fullPath)) failures.push(`${file}:${line}: ${target}`);
}

for (const file of files) {
  const text = withoutCode(readFileSync(resolve(root, file), 'utf8'));
  const lineAt = index => text.slice(0, index).split('\n').length;
  // Links used by this repository: inline destinations and reference definitions.
  for (const match of text.matchAll(/!?\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^\s)]+)(?:\s+["'][^\n]*?["'])?\s*\)/g))
    check(file, match[1].replace(/^<|>$/g, ''), lineAt(match.index));
  for (const match of text.matchAll(/^\s*\[[^\]\n]+\]:\s*(<[^>\n]+>|\S+)/gm))
    check(file, match[1].replace(/^<|>$/g, ''), lineAt(match.index));
}
console.log(`Checked ${files.length} first-party Markdown files and ${checked} local file links.`);
if (skipped.size) console.log(`External submodule documentation excluded: ${[...skipped].sort().join(', ')}`);
if (localEvidence.length) console.log(`Excluded ${localEvidence.length} machine-local evidence/workspace references (not validated).`);
if (process.argv.includes('--show-local-evidence')) console.log(localEvidence.join('\n'));
if (failures.length) {
  console.error(`Broken local links (${failures.length}):\n${failures.join('\n')}`);
  process.exitCode = 1;
} else console.log('PASS: all checked local destinations exist. Remote URLs and heading anchors are not validated.');
