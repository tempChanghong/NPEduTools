import assert from 'node:assert/strict'
import { execFile, spawn } from 'node:child_process'
import { createServer } from 'node:http'
import { mkdir, mkdtemp, readFile, stat, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { promisify } from 'node:util'

const project = dirname(fileURLToPath(import.meta.url))
const executable = join(project, 'bin', 'Release', 'net10.0-windows', 'NPEduTools.EdgeRestrictionPrototype.exe')
const edge = join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe')
const output = resolve(process.argv[2] ?? '.artifacts/edge-website-restriction/uia')
const exec = promisify(execFile)
await mkdir(output, { recursive: true })
const requests = new Set()
let lessonRequests = 0
const server = createServer((request, response) => {
  requests.add(request.url)
  if (request.url === '/lesson') {
    response.setHeader('Content-Type', 'text/html; charset=utf-8')
    response.end(`<!doctype html><title>课堂讲义</title><h1>课堂讲义</h1><p>本节课继续。</p>
      <script>setInterval(() => {
        if (document.visibilityState === 'visible') {
          fetch('/restricted-resource', { cache: 'no-store' }).catch(() => {});
        }
      }, 700);</script>`)
    return
  }
  if (request.url === '/restricted-resource') {
    lessonRequests++
    response.writeHead(403, { 'Cache-Control': 'no-store' })
    response.end()
    return
  }
  if (request.url === '/network-error') {
    request.socket.destroy()
    return
  }
  response.setHeader('Content-Type', 'text/html; charset=utf-8')
  response.end('<title>ERR_BLOCKED_BY_ADMINISTRATOR</title><h1>ERR_BLOCKED_BY_ADMINISTRATOR</h1>')
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
const origin = `http://127.0.0.1:${server.address().port}`

try {
  const failures = []
  for (const [name, diagnosticPage] of [
    ['page-text'], ['network-error'], ['native-admin-diagnostic', 'edge://network-error/-22'], ['lesson']
  ]) {
    try {
      await probe(name, diagnosticPage)
    } catch (error) {
      failures.push(error)
    }
  }
  if (failures.length) throw new AggregateError(failures, 'One or more Edge observations failed.')
} finally {
  server.closeAllConnections()
  server.close()
}

async function probe(name, diagnosticPage) {
  const url = `${origin}/${name}`
  const evidence = join(output, name)
  await mkdir(evidence, { recursive: true })
  const profile = await mkdtemp(join(evidence, 'guest-'))
  const browser = spawn(edge, [
    `--user-data-dir=${profile}`, '--guest', '--disable-sync', '--disable-extensions',
    '--no-first-run', '--no-default-browser-check', '--disable-background-networking',
    '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1', '--no-proxy-server',
    diagnosticPage ?? url
  ], { stdio: 'ignore' })
  let pid
  try {
    if (diagnosticPage) {
      await delay(1800)
    } else {
      for (let attempt = 0; attempt < 60 && !requests.has(`/${name}`); attempt++) {
        await delay(200)
      }
      assert(requests.has(`/${name}`), 'Edge loaded the local case.')
    }
    pid = await findBrowser(profile)
    assert(pid > 0, 'Found the actual Edge browser for the isolated profile.')
    const watchedUrl = name === 'lesson' ? `${origin}/restricted-resource` : url
    const priorRequests = lessonRequests
    const observer = spawn(executable, ['--probe-foreground', String(pid), watchedUrl, evidence],
      { windowsHide: true, stdio: 'inherit' })
    const code = await new Promise((resolve, reject) => {
      observer.once('error', reject)
      observer.once('exit', resolve)
    })
    assert.equal(code, 0)
    const report = JSON.parse(await readFile(join(evidence, 'foreground-uia.json'), 'utf8'))
    assert(await stat(join(profile, 'DevToolsActivePort')).then(() => false, () => true), 'Edge runs without a debugging port.')
    assert.equal(report.warningsShown, 0, 'No warning should overlay this case.')
    const unavailable = Object.keys(report.outcomes).some(outcome => outcome.startsWith('no-unique-active-document-'))
    console.log(JSON.stringify({ name, documentUnavailable: unavailable, ...report }))
    if (name === 'lesson') {
      const visibleLessonRequests = lessonRequests - priorRequests
      assert(visibleLessonRequests >= 2, 'A visible lesson issued background requests during observation.')
      assert(Object.keys(report.outcomes).some(outcome =>
        outcome.startsWith('no-unique-active-document-') || outcome === 'no-internal-error-document' ||
        outcome === 'document-url-not-exposed'),
        'The observer inspected the classroom Edge window.')
      await writeFile(join(evidence, 'classroom-result.json'), JSON.stringify({
        visibleLessonRequests, resourceStatus: 403, warningsShown: report.warningsShown,
        source: 'local-http-fixture'
      }, null, 2))
    } else if (name === 'page-text') {
      assert(unavailable || report.outcomes['external-document-at-target'] > 0, JSON.stringify(report))
    } else {
      assert(unavailable || report.outcomes['external-document-at-target'] > 0 || report.outcomes['no-internal-error-document'] > 0
        || report.outcomes['document-url-not-exposed'] > 0 || report.outcomes['no-admin-error-code'] > 0
        || report.outcomes['diagnostic-document-url'] > 0 || report.outcomes['error-destination-not-exposed'] > 0,
      JSON.stringify(report))
    }
  } finally {
    const owned = pid ?? await findBrowser(profile)
    if (owned) {
      await exec('powershell.exe', ['-NoProfile', '-Command',
        `$browser = Get-Process -Id ${owned} -ErrorAction SilentlyContinue
        if ($browser) { [void]$browser.CloseMainWindow() }`], { windowsHide: true })
    }
    if (browser.exitCode === null && browser.pid !== owned) browser.kill()
  }
}

async function findBrowser(profile) {
  const { stdout } = await exec('powershell.exe', ['-NoProfile', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
      Where-Object { $_.CommandLine -and $_.CommandLine.Contains($env:NPEP_UIA_SMOKE_PROFILE) -and $_.CommandLine -notmatch '\x2d\x2dtype=' } |
      Select-Object -First 1 -ExpandProperty ProcessId`], {
    windowsHide: true, env: { ...process.env, NPEP_UIA_SMOKE_PROFILE: profile }
  })
  return Number(stdout.trim())
}
