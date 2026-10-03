import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {randomUUID, randomBytes} from 'node:crypto';

const [desktop, backend] = process.argv.slice(2).map(p=>resolve(p));
const schema = async path => JSON.parse(await readFile(path,'utf8'));
assert.deepEqual(await schema(resolve(desktop,'docs/npep/n1-wire.schema.json')),
  await schema(resolve(backend,'domain/npep/wire.schema.json')), 'Desktop/backend pairing schema drift');
const {validate} = await import(pathToFileURL(resolve(backend,'domain/npep/wire.js')));
const claim = {requestId:randomUUID(),serverInstanceId:randomUUID(),deploymentEpoch:randomUUID(),installationId:randomUUID(),
  deviceName:'契约测试',appVersion:'test',pairingSecret:randomBytes(32).toString('base64url'),requestedCapabilities:['device.status'],userCode:'ABCD2345'};
assert.ok(validate('claimScreenPairing',claim));
for (const extra of [{schoolId:'school'},{screenBindingId:'screen'},{userCode:'INVALID!'}]) assert.equal(validate('claimScreenPairing',{...claim,...extra}),false);
assert.ok(validate('issueScreenPairing',{requestId:randomUUID()}));
assert.ok(validate('setScreenPairingAccess',{requestId:randomUUID(),enabled:true,expectedRevision:1}));
const batch = {requestId:randomUUID(),termId:'term',targetType:'SCHOOL',targetId:null,enabled:true};
assert.ok(validate('previewPairingAccessBatch',batch));
assert.ok(validate('setPairingAccessBatch',{...batch,previewDigest:'a'.repeat(64)}));
assert.ok(validate('previewPairingAccessBatch',{...batch,targetType:'GRADE',targetId:'grade'}));
for (const extra of [{targetType:'GRADE',targetId:null},{targetId:'grade'},{termId:''},{command:'anything'}]) assert.equal(validate('previewPairingAccessBatch',{...batch,...extra}),false);
assert.equal(validate('setPairingAccessBatch',batch),false);
console.log('Pairing contract passed: schema parity, fixed assignment, strict code boundary and explicit batch scope.');
