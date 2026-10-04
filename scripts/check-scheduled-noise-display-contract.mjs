// Real desktop service snapshots (synthetic capture) are fed to the production
// web predicates. No credentials, microphone, database or deployment is used.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

const [webRoot, fixtureRoot] = process.argv.slice(2);
if (!webRoot || !fixtureRoot) throw new Error('Usage: node check-scheduled-noise-display-contract.mjs <WebRoot> <fixtures>');
const {nativeScheduledDisplayCandidate: candidate, nativeDisplayPhase: phase,
  schoolCalendarMilliseconds: calendar, schoolRemainingSeconds: remaining} =
  await import(pathToFileURL(resolve(webRoot, 'src/utils/scheduledNoiseDisplay.js')));
let checks = 0;
const equal = (actual, expected, message) => { assert.equal(actual, expected, message); checks++; };
const snapshots = new Map();
for (const name of ['starting', 'active', 'stopped', 'resumed', 'manual', 'naturally-ended', 'failed']) {
  const fixture = JSON.parse(await readFile(resolve(fixtureRoot, `${name}.json`), 'utf8'));
  equal(fixture.source, 'synthetic-desktop-services', `${name}: provenance`);
  const noise = {provider: 'native', online: true, status: fixture.noiseStatus};
  const schedule = {supported: true, online: true, applied: true, status: fixture.scheduleStatus};
  equal(Boolean(candidate(noise, schedule)), fixture.expectedAutoEligible, `${name}: actual desktop entry eligibility`);
  if (fixture.previousScheduleStatus) {
    const previous = fixture.previousScheduleStatus;
    const context = {provider: 'native', sessionId: previous.sessionId, window: previous.window,
      windowKey: JSON.stringify([previous.window.start, previous.window.end])};
    equal(phase(noise, schedule, context), fixture.expectedPhase, `${name}: actual desktop end phase`);
    equal(phase({...noise, status: {...noise.status, sessionId: 'another-session'}}, schedule, context),
      'unknown', `${name}: another session cannot end this display`);
  }
  snapshots.set(name, {noise, schedule});
}
const {noise, schedule} = snapshots.get('active');
equal(Boolean(candidate(noise, {...schedule, applied: false})), true, 'Pending policy does not negate actual capture');
equal(candidate({...noise, online: false}, schedule), null, 'Offline noise is not eligible');
equal(candidate(noise, {...schedule, online: false}), null, 'Offline schedule is not eligible');
equal(candidate({...noise, status: {...noise.status, sessionId: 'another-session'}}, schedule), null,
  'Non-atomic mismatched sessions are not eligible');
equal(candidate(noise, {...schedule, status: {...schedule.status, clockReady: false}}), null, 'Invalid school clock');
equal(candidate(noise, {...schedule, status: {...schedule.status, dateNeedsReview: true}}), null, 'Unreviewed school date');
equal(calendar('2026-10-01T19:00:02.500') - calendar('2026-10-01T19:00:02.000'), 500, 'School milliseconds preserved');
equal(calendar('2026-10-01T19:00:02.000Z'), null, 'School calendar is not a UTC instant');
equal(calendar('2026-02-30T19:00:02.000'), null, 'Invalid date rejected');
equal(remaining('2026-10-01T23:59:59.500', '2026-10-02T00:00:00.000', 500), 0, 'Cross-midnight monotonic countdown');
equal(remaining(schedule.status.schoolNow, schedule.status.window.end), 3598, 'Actual .fff desktop countdown');
console.log(`Desktop/web scheduled display contract: ${checks} checks passed, 7 actual service fixtures.`);
