import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
const [desktop, web, backend] = process.argv.slice(2).map(p => resolve(p));
const read = (root, path) => readFile(resolve(root, path), 'utf8').then(JSON.parse);
assert.deepEqual(await read(desktop, 'docs/npep/n4-exam-plan.schema.json'), await read(backend, 'domain/npep/exam-plan.schema.json'));
const {validateExamPlan} = await import(pathToFileURL(resolve(backend, 'domain/npep/examPlans.js')));
const {readExamPlan} = await import(pathToFileURL(resolve(web, 'src/utils/npepExamPlans.js')));
const id = '12345678-1234-4234-8234-123456789abc';
const request = {requestId: id, context: {identity: {serverInstanceId: id, deploymentEpoch: id, deviceId: id, bindingRevision: 1, credentialGeneration: 1}, runId: id, sessionId: id, statusEpoch: 1, controlEpoch: id},
  consentId: id, policyRevision: 1, revision: 1, ...await readExamPlan(new File(['{"examName":"测试"}'], '方案.json'))};
assert.ok(validateExamPlan('createRequest', request));
for (const extra of [{replaceExisting: true}, {path: 'C:/x.exe'}, {shell: 'cmd'}, {deviceIds: [id]}]) assert.ok(!validateExamPlan('createRequest', {...request, ...extra}));
console.log('Exam plan schema parity, browser byte input and forbidden authority extensions passed.');
