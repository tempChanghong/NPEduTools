import assert from 'node:assert/strict';
import {readFileSync, mkdtempSync, writeFileSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';
const source = readFileSync(new URL('./NPEP-N1-PERMISSION-RETRY.sh', import.meta.url), 'utf8');
const start = source.indexOf('    if ! (\n');
const end = source.indexOf('    ); then', start);
assert.ok(start >= 0 && end > start);
const guard = source.slice(start, end + '    ); then'.length);
const directory = mkdtempSync(join(tmpdir(), 'npep-guard-'));
const backup = join(directory, 'fake.dump');
writeFileSync(backup, 'fake');
const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
try {
  const scenarios = ['valid', 'source_failure', 'backend_ref', 'frontend_ref', 'backup_missing', 'backend_image', 'frontend_image', 'field_missing'];
  for (const scenario of scenarios) {
    const input = `set -eu
scenario=${quote(scenario)}
RUNTIME_DIR=/unused
previous_backend=old-backend
previous_frontend=old-frontend
previous_backend_image=image-backend
previous_frontend_image=image-frontend
source() {
  PREVIOUS_BACKEND_REF=old-backend
  PREVIOUS_FRONTEND_REF=old-frontend
  BACKUP_FILE=${quote(backup.replaceAll('\\', '/'))}
  BACKEND_ROLLBACK_TAG=backend-tag
  FRONTEND_ROLLBACK_TAG=frontend-tag
  case "$scenario" in
    source_failure) return 1;;
    backend_ref) PREVIOUS_BACKEND_REF=wrong;;
    frontend_ref) PREVIOUS_FRONTEND_REF=wrong;;
    backup_missing) BACKUP_FILE=/no-such-npep-test-backup;;
    field_missing) unset PREVIOUS_BACKEND_REF;;
  esac
  return 0
}
docker() {
  case "\${@: -1}" in
    backend-tag) if [ "$scenario" = backend_image ]; then echo wrong; else echo image-backend; fi;;
    frontend-tag) if [ "$scenario" = frontend_image ]; then echo wrong; else echo image-frontend; fi;;
    *) return 1;;
  esac
}
${guard}
  echo rejected
  exit 17
fi
echo accepted
`;
    const shell = process.platform === 'win32' ? 'C:/Program Files/Git/bin/bash.exe' : 'bash';
    const result = spawnSync(shell, ['--noprofile', '--norc', '-s'], {input, encoding:'utf8'});
    assert.equal(result.status, scenario === 'valid' ? 0 : 17, `${scenario}: ${result.stderr}`);
    console.log(`${scenario}: ${result.stdout.trim()}`);
  }
  console.log('8 rollback guard cases passed; no real deployment commands executed');
} finally { rmSync(directory, {recursive:true, force:true}); }
