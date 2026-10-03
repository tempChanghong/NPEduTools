import {spawnSync} from 'node:child_process';
import {readFile, realpath, access, writeFile} from 'node:fs/promises';
import {resolve, join} from 'node:path';
import {fileURLToPath} from 'node:url';

export async function inspectPairingSources(roots, expected = {}, requireClean = false) {
  const actualRoots = Object.fromEntries(await Promise.all(Object.entries(roots).map(async([key,path])=>[key,await realpath(path)])));
  if (new Set(Object.values(actualRoots).map(p=>process.platform==='win32'?p.toLowerCase():p)).size !== 3) throw new Error('Three distinct repository roots are required.');
  const git = (root,args)=>{
    const r=spawnSync('git',['-C',root,...args],{encoding:'utf8',windowsHide:true});
    if(r.error||r.status!==0)throw new Error('Unable to inspect repository source.');return r.stdout.trim();
  };
  await access(join(actualRoots.desktop,'src/NPEduTools.App/NPEduTools.App.csproj'));
  for (const [key,name] of [['web','classworks'],['backend','ClassworksKV']]) {
    if(JSON.parse(await readFile(join(actualRoots[key],'package.json'),'utf8')).name!==name)throw new Error(`Wrong ${key} product.`);
  }
  const sources={};
  for(const key of ['desktop','web','backend']) {
    const root=actualRoots[key];
    if(await realpath(git(root,['rev-parse','--show-toplevel']))!==root)throw new Error(`${key} must point to the repository root.`);
    const commit=git(root,['rev-parse','HEAD']), dirty=!!git(root,['status','--porcelain','--untracked-files=normal']);
    if(expected[key] && !/^[0-9a-f]{40}$/i.test(expected[key]))throw new Error(`${key} expected commit must be a full SHA.`);
    if(requireClean && !expected[key])throw new Error(`${key} requires an explicit commit in CI.`);
    if(expected[key] && expected[key].toLowerCase()!==commit.toLowerCase())throw new Error(`${key} source commit mismatch.`);
    if(requireClean && dirty)throw new Error(`${key} has uncommitted changes; cannot certify an immutable CI combination.`);
    sources[key]={root,commit,dirty};
  }
  return {sources,immutable:requireClean,localChanges:Object.values(sources).some(s=>s.dirty)};
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const values={},args=process.argv.slice(2);
  for(let i=0;i<args.length;i++){
    const key=args[i];if(key==='--require-clean')values.clean=true;
    else if(['--desktop-root','--web-root','--backend-root','--desktop-commit','--web-commit','--backend-commit','--output'].includes(key)&&args[i+1]&&!args[i+1].startsWith('--'))values[key]=args[++i];
    else throw new Error(`Unknown or incomplete source option: ${key}`);
  }
  const result=await inspectPairingSources(Object.fromEntries(['desktop','web','backend'].map(k=>[k,values[`--${k}-root`]])),
    Object.fromEntries(['desktop','web','backend'].map(k=>[k,values[`--${k}-commit`]])),!!values.clean);
  if(values['--output'])await writeFile(values['--output'],JSON.stringify(result,null,2));
  console.log(`Pairing sources: ${Object.entries(result.sources).map(([k,v])=>`${k}=${v.commit}${v.dirty?' (local changes)':''}`).join(', ')}`);
}
