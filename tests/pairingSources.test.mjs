import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';
import {inspectPairingSources} from '../scripts/check-npep-pairing-sources.mjs';

test('Pairing source gate certifies a precise clean combination and rejects drift',async t=>{
  const temporary=await mkdtemp(join(tmpdir(),'pairing-source-gate-'));
  t.after(()=>rm(temporary,{recursive:true,force:true}));
  const roots=Object.fromEntries(['desktop','web','backend'].map(k=>[k,join(temporary,k)]));
  for(const [key,root] of Object.entries(roots)){
    await mkdir(root,{recursive:true});
    await writeFile(join(root,'tracked.txt'),'source\r\n');
    if(key==='desktop'){await mkdir(join(root,'src/NPEduTools.App'),{recursive:true});await writeFile(join(root,'src/NPEduTools.App/NPEduTools.App.csproj'),'<Project />');}
    else await writeFile(join(root,'package.json'),JSON.stringify({name:key==='web'?'classworks':'ClassworksKV'}));
    for(const args of [['init'],['config','core.autocrlf','true'],['add','.'],['-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-m','synthetic source fixture']]){
      const r=spawnSync('git',['-C',root,...args],{windowsHide:true,encoding:'utf8'});assert.equal(r.status,0,r.stderr);
    }
  }
  const initial=await inspectPairingSources(roots);
  const expected=Object.fromEntries(Object.entries(initial.sources).map(([k,v])=>[k,v.commit]));
  assert.equal((await inspectPairingSources(roots,expected,true)).immutable,true);
  await assert.rejects(inspectPairingSources(roots,{...expected,web:'main'},true),/full SHA/);
  await assert.rejects(inspectPairingSources(roots,{...expected,backend:'0'.repeat(40)},true),/mismatch/);
  await assert.rejects(inspectPairingSources(roots,{},true),/explicit commit/);
  await assert.rejects(inspectPairingSources({...roots,backend:roots.web}),/distinct/);
  const configure=spawnSync('git',['-C',roots.web,'config','core.autocrlf','true'],{windowsHide:true});
  assert.equal(configure.status,0);
  await writeFile(join(roots.web,'tracked.txt'),'source\n');
  assert.equal((await inspectPairingSources(roots,expected,true)).immutable,true,'CRLF/LF with identical Git content is clean');
  await writeFile(join(roots.web,'tracked.txt'),'changed\n');
  await assert.rejects(inspectPairingSources(roots,expected,true),/uncommitted changes/);
  const stage=spawnSync('git',['-C',roots.web,'add','tracked.txt'],{windowsHide:true});
  assert.equal(stage.status,0);
  await writeFile(join(roots.web,'tracked.txt'),'source\n');
  await assert.rejects(inspectPairingSources(roots,expected,true),/uncommitted changes/,'Staged changes stay dirty even when working content matches HEAD');
  await writeFile(join(roots.web,'uncommitted.txt'),'synthetic change');
  assert.equal((await inspectPairingSources(roots)).localChanges,true);
  await assert.rejects(inspectPairingSources(roots,expected,true),/uncommitted changes/);
  await writeFile(join(roots.backend,'package.json'),JSON.stringify({name:'unrelated'}));
  await assert.rejects(inspectPairingSources(roots),/Wrong backend product/);
});
