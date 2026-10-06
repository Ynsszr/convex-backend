// One final native-first acceptance command. Frozen Fable sources are read only
// when the actual DBM mixed deployment graph requires its existing neighbours.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash,randomBytes} from 'node:crypto';
import fs from 'node:fs/promises';
import {createWriteStream} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';
import {pinnedFramework} from './pinned_framework.mjs';

const root=fileURLToPath(new URL('../../..',import.meta.url));
const [configuration,resumeEvidence]=process.argv.slice(2);assert.ok(configuration&&path.isAbsolute(configuration),'absolute final acceptance configuration required');
assert.ok(!resumeEvidence||path.isAbsolute(resumeEvidence),'absolute runtime checkpoint evidence required');
const config=JSON.parse(await fs.readFile(configuration,'utf8'));
for(const name of ['dotnet','host','runtimeCapsules','componentCapsules','projectCli','negativeAssembly','dbm','dbmCapsules','nativeSchema'])
  assert.ok(config[name]&&path.isAbsolute(config[name]),`absolute ${name} required`);
const state=await fs.mkdtemp('/tmp/convex-dotnet-final-native-');await fs.chmod(state,0o700);
const binary=path.join(root,'target/debug/convex-local-backend'),hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const {ConvexHttpClient}=await import(path.join(root,'npm-packages/convex/dist/esm/browser/index.js'));
const {makeFunctionReference}=await import(path.join(root,'npm-packages/convex/dist/esm/server/index.js'));
const proofNode=process.env.CONVEX_DOTNET_PROOF_NODE||process.execPath;
const env={PATH:path.dirname(proofNode)+path.delimiter+process.env.PATH,HOME:state,LANG:'C.UTF-8',CI:'1',SENTRY_DSN:'',DISABLE_BEACON:'true',RUST_LOG:'warn'};
const secret=randomBytes(32).toString('hex'),deployment='final-native-proof';let key='',backend;
const summary={state,base:'df8338dd8974674cf0c4cfb42aff4b50f790bc9c',checks:[]};
const log=(name,detail)=>{summary.checks.push({name,detail});console.log(`PASS ${name}`);};
async function run(program,args,extra={}){
  return await new Promise((resolve,reject)=>{
    const child=spawn(program,args,{cwd:state,env:{...env,...extra},stdio:['ignore','pipe','pipe']});let output='';
    for(const stream of [child.stdout,child.stderr])stream.on('data',chunk=>output=(output+chunk.toString()).slice(-1024*1024));
    child.on('error',reject);child.on('exit',code=>resolve({code,output:output.replaceAll(key||'never-secret','[isolated-key]')}));
  });
}
async function freePort(){return await new Promise(resolve=>{
  const server=net.createServer();server.listen(0,'127.0.0.1',()=>{const port=server.address().port;server.close(()=>resolve(port));});
});}
const port=await freePort(),sitePort=await freePort(),url=`http://127.0.0.1:${port}`,site=`http://127.0.0.1:${sitePort}`;
const framework=await pinnedFramework(config.dotnet),artifacts=[];
for(const file of await fs.readdir(path.dirname(config.host),{withFileTypes:true}))if(file.isFile()){
  const p=path.join(path.dirname(config.host),file.name);artifacts.push({path:p,sha256:hash(await fs.readFile(p))});
}
const manifest={version:1,maxWorkers:8,worker:{program:config.dotnet,programSha256:hash(await fs.readFile(config.dotnet)),
  arguments:[config.host],artifacts,framework,profile:'restricted-first-party',memoryMiB:256,invocationTimeoutMs:16000,maxInvocations:500},functions:[]};
const manifestFile=path.join(state,'runtime.json');await fs.writeFile(manifestFile,JSON.stringify(manifest),{mode:0o600});
async function start(label,file=manifestFile){
  const output=createWriteStream(path.join(state,`backend-${label}.log`));
  backend=spawn(binary,[path.join(state,'database.sqlite3'),'--interface','127.0.0.1','--port',String(port),'--site-proxy-port',String(sitePort),
    '--instance-name',deployment,'--instance-secret',secret,'--local-storage',path.join(state,'storage'),'--disable-beacon'],
    {cwd:state,env:{...env,CONVEX_DOTNET_MANIFEST:file},stdio:['ignore','pipe','pipe']});backend.stdout.pipe(output);backend.stderr.pipe(output);
  for(let i=0;i<150;i++){
    if(backend.exitCode!==null)throw new Error(`private backend readiness failed; ${state}`);
    try{if((await fetch(url+'/version',{signal:AbortSignal.timeout(500)})).ok)return;}catch{}await delay(100);
  }throw new Error('private native backend readiness deadline');
}
async function stop(){
  if(!backend||backend.exitCode!==null)return;const child=backend,finished=new Promise(resolve=>child.once('exit',resolve));child.kill('SIGTERM');
  await Promise.race([finished,delay(5000)]);if(child.exitCode===null){child.kill('SIGKILL');await finished;}backend=undefined;
}
async function owner(route,body){
  const response=await fetch(url+route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
  const value=await response.json();assert.ok(response.ok,`private owning ${route} refused (${value.code})`);return value;
}
function pattern(size){const bytes=Buffer.allocUnsafe(size);for(let offset=0;offset<size;offset++)bytes[offset]=offset%251;return bytes;}
async function streamDigest(method='GET',size=9*1024*1024){
  const response=await fetch(site+'/native/stream',{method,headers:{'x-native-size':String(size)},signal:AbortSignal.timeout(30000)});
  assert.equal(response.status,200);const digest=createHash('sha256');let length=0;
  assert.ok(method==='HEAD'||response.body,'native body-producing response has a stream');
  if(response.body)for await(const chunk of response.body){digest.update(chunk);length+=chunk.length;}
  assert.equal(length,method==='HEAD'?0:size);
  if(method!=='HEAD')assert.equal(digest.digest('hex'),hash(pattern(size)));
}
try{
  summary.backendSha256=hash(await fs.readFile(binary));summary.hostArtifacts=artifacts;summary.framework=framework;
  summary.cliSha256=hash(await fs.readFile(config.projectCli));
  if(resumeEvidence){
    const checkpoint=JSON.parse(await fs.readFile(resumeEvidence,'utf8'));
    const expected=[
      'complete native root/child project enters original graph, schema, function and atomic activation owners without JavaScript stubs',
      'selected native component namespace, same durable address and owning direct-call authority',
      'native9MiB HTTP response streams beyond former buffer, exact bytes/hash and bodyless HEAD',
      'native9MiB bounded upload/download retains original storage hash/metadata/delete owners',
      'native streamed client cancellation releases payload permits and subsequent request succeeds',
      'assembly containing forbidden ambient roots refuses before getter evaluation and leaves active native graph intact',
      'native graph and retained component data restart from persisted capsules under pinned framework',
      'restricted worker refuses incomplete runtime closure before invocation'];
    assert.deepEqual(checkpoint.checks.map(item=>item.name),expected,'complete runtime checkpoint required');
    const checkpointBackend=config.runtimeCheckpointBackendSha256??summary.backendSha256;
    assert.match(checkpointBackend,/^[a-f0-9]{64}$/,'exact retained runtime checkpoint backend digest required');
    assert.equal(checkpoint.backendSha256,checkpointBackend);assert.equal(checkpoint.cliSha256,summary.cliSha256);
    assert.deepEqual(checkpoint.hostArtifacts,artifacts);assert.deepEqual(checkpoint.framework,framework);
    summary.runtimeEvidence={path:resumeEvidence,sha256:hash(await fs.readFile(resumeEvidence)),backendSha256:checkpoint.backendSha256,
      applicationBackendSha256:summary.backendSha256,scope:'historical runtime checkpoint; current backend runs application phase only'};
    summary.checks.push(...checkpoint.checks);
    console.log(`Retained8 historical runtime groups on ${checkpoint.backendSha256}; application phase runs ${summary.backendSha256}, with identical frozen Host/framework/CLI.`);
  }else{
  key=(await run(binary,['keygen','admin-key','--instance-name',deployment,'--instance-secret',secret])).output.trim();
  await start('native');
  const environment=await fetch(url+'/api/update_environment_variables',{method:'POST',headers:{'Content-Type':'application/json',Authorization:`Convex ${key}`},
    body:JSON.stringify({changes:[{name:'DBM_OIDC_ISSUER',value:'https://native-final-proof.invalid'},{name:'DBM_OIDC_AUDIENCE',value:'native-final-proof'}]}),signal:AbortSignal.timeout(10000)});
  assert.ok(environment.ok,'owning synthetic fixture auth environment setup');
  const projectRoot=path.join(state,'native-root'),childRoot=path.join(state,'native-child');
  await fs.cp(config.runtimeCapsules,projectRoot,{recursive:true});await fs.mkdir(childRoot);
  await fs.copyFile(path.join(config.componentCapsules,'app.js'),path.join(projectRoot,'convex.config.js'));
  for(const [source,target] of [['child.js','convex.config.js'],['counter.js','counter.js'],['schema.js','schema.js']])
    await fs.copyFile(path.join(config.componentCapsules,source),path.join(childRoot,target));
  const projectFile=path.join(state,'native-project.json');await fs.writeFile(projectFile,JSON.stringify({version:1,functionsDirectory:'convex',
    rootDirectory:projectRoot,rootDependencies:['../probe'],components:[{definitionPath:'../probe',capsuleDirectory:childRoot,dependencies:[]}]}),{mode:0o600});
  const activated=await run(config.dotnet,[config.projectCli,'deploy-project',url,projectFile,'PROOF_NATIVE_ADMIN_KEY'],
    {PROOF_NATIVE_ADMIN_KEY:key,CONVEX_DOTNET_STATE_DIRECTORY:path.join(state,'client-journal')});
  assert.equal(activated.code,0,activated.output);assert.equal(JSON.parse(activated.output).outcome,'confirmed');
  const modules=(await owner('/api/get_config_hashes',{adminKey:key})).moduleHashes;
  assert.ok(modules.length>0);assert.ok(modules.every(module=>module.environment==='dotNet'));
  log('complete native root/child project enters original graph, schema, function and atomic activation owners without JavaScript stubs');
  const client=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false}),ref=name=>makeFunctionReference(`Counter:${name}`);
  assert.deepEqual(await client.mutation(ref('componentIncrement'),{name:'native-final',amount:3}),{name:'native-final',value:3});
  assert.deepEqual(await client.query(ref('componentRead'),{name:'native-final'}),{name:'native-final',value:3});
  assert.equal(await client.query(ref('read'),{name:'native-final'}),null);
  await assert.rejects(client.function('counter:read','probe',{name:'native-final'}));
  log('selected native component namespace, same durable address and owning direct-call authority');
  await streamDigest();await streamDigest('HEAD');
  log('native9MiB HTTP response streams beyond former buffer, exact bytes/hash and bodyless HEAD');
  const size=9*1024*1024,stored=await client.action(ref('storageStreamRoundtrip'),{size});
  assert.equal(stored.length,BigInt(size));assert.equal(stored.sha256,createHash('sha256').update(pattern(size)).digest('base64'));
  assert.equal(stored.contentType,'application/octet-stream');
  log('native9MiB bounded upload/download retains original storage hash/metadata/delete owners');
  const interrupted=await fetch(site+'/native/stream',{headers:{'x-native-size':String(20*1024*1024)},signal:AbortSignal.timeout(30000)});
  const reader=interrupted.body.getReader();assert.ok((await reader.read()).value.length>0);await reader.cancel();
  await streamDigest('GET',65537);
  log('native streamed client cancellation releases payload permits and subsequent request succeeds');
  const negativeBytes=await fs.readFile(config.negativeAssembly),negativeSource=JSON.stringify({format:'convex-dotnet-capsule',version:1,moduleKind:'functions',export:null,
    definition:null,assembly:{name:path.basename(config.negativeAssembly),sha256:hash(negativeBytes),bytes:negativeBytes.toString('base64')},assemblyDependencies:[]});
  const negativeGraph={functions:'convex',appDefinition:{definition:null,dependencies:[],schema:null,changedModules:[{path:'RuntimeFixture/NativeNegative.js',source:negativeSource,sourceMap:null,environment:'dotNet'}],unchangedModuleHashes:[],udfServerVersion:'1.46.0'},
    componentDefinitions:[],nodeDependencies:[],dryRun:false};
  // Admission refusal happens during selected declaration catalogue evaluation,
  // before any ambient getter or owning activation can execute.
  const rejected=await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...negativeGraph,adminKey:key}),signal:AbortSignal.timeout(30000)});
  const rejection=await rejected.json();assert.equal(rejected.status,400);assert.equal(rejection.code,'InvalidNativeCapsule');
  assert.match(rejection.message,/Native module admission failed/);
  assert.match(await fs.readFile(path.join(state,'backend-native.log'),'utf8'),/Forbidden or unresolvable native call System\.IO\.File\.Exists/);
  assert.deepEqual((await owner('/api/get_config_hashes',{adminKey:key})).moduleHashes,modules);
  assert.deepEqual(await client.query(ref('componentRead'),{name:'native-final'}),{name:'native-final',value:3});
  log('assembly containing forbidden ambient roots refuses before getter evaluation and leaves active native graph intact');
  await stop();await start('restart');assert.deepEqual(await client.query(ref('componentRead'),{name:'native-final'}),{name:'native-final',value:3});
  await streamDigest('GET',65537);log('native graph and retained component data restart from persisted capsules under pinned framework');
  await stop();
  const unsigned=structuredClone(manifest);unsigned.worker.framework.artifacts=unsigned.worker.framework.artifacts.slice(1);
  const unsignedFile=path.join(state,'unsigned-runtime.json');await fs.writeFile(unsignedFile,JSON.stringify(unsigned),{mode:0o600});
  let refused=false;try{await start('unsigned-runtime',unsignedFile);}catch{refused=true;}finally{await stop();}assert.ok(refused);
  assert.match(await fs.readFile(path.join(state,'backend-unsigned-runtime.log'),'utf8'),/native runtime closure has added, missing or unpinned files/);
  log('restricted worker refuses incomplete runtime closure before invocation');
  }
  // The existing actual DBM harness adds the newly ported fourth owner and
  // runs once as this combined command's final application acceptance phase.
  const dbm=await run(process.execPath,[path.join(root,'crates/dotnet_executor/proof/dbm_backend_parity.mjs'),config.dotnet,config.host,config.dbm,config.dbmCapsules,config.projectCli,config.nativeSchema,
    ...(config.dbmReferenceConvexDirectory?[config.dbmReferenceConvexDirectory]:[])]);
  assert.equal(dbm.code,0,dbm.output);console.log(dbm.output.trim());
  const evidence=dbm.output.match(/Evidence: (\/tmp\/[^\n]+\/summary\.json)/);assert.ok(evidence);
  summary.applicationEvidence=evidence[1];log('actual DBM four native function owners and31-table native schema acceptance');
  console.log(`Final combined native acceptance passed. Evidence: ${path.join(state,'summary.json')}`);
}finally{await stop();await fs.writeFile(path.join(state,'summary.json'),JSON.stringify(summary,null,2));}
