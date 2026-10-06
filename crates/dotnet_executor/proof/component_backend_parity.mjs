// Owning mixed app/component admission and immutable activation settlement.
// Every process, credential, port and data file belongs to this private proof.
import assert from 'node:assert/strict';
import {pinnedFramework} from './pinned_framework.mjs';
import {spawn} from 'node:child_process';
import {createHash,randomBytes} from 'node:crypto';
import fs from 'node:fs/promises';
import {createWriteStream} from 'node:fs';
import net from 'node:net';
import {createServer as httpServer} from 'node:http';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';

const root=fileURLToPath(new URL('../../..',import.meta.url));
const [dotnet,host,fixture,componentFixture,capsules,nativeCli,receiptCli]=process.argv.slice(2);
assert.ok([dotnet,host,fixture,componentFixture,capsules,nativeCli].every(p=>p&&path.isAbsolute(p)),
  'usage: component_backend_parity.mjs DOTNET HOST_DLL ROOT_FIXTURE FABLE_CHILD_FIXTURE NATIVE_COMPONENT_CAPSULES CLI_DLL');
assert.ok(receiptCli===undefined||path.isAbsolute(receiptCli),'receipt CLI must be absolute');
const {ConvexHttpClient,ConvexClient}=await import(path.join(root,'npm-packages/convex/dist/esm/browser/index.js'));
const {makeFunctionReference}=await import(path.join(root,'npm-packages/convex/dist/esm/server/index.js'));
const binary=path.join(root,'target/debug/convex-local-backend');
const state=await fs.mkdtemp('/tmp/convex-dotnet-component-parity-');await fs.chmod(state,0o700);
const project=path.join(state,'project');await fs.cp(fixture,project,{recursive:true});
const component=path.join(project,'probe');await fs.mkdir(component,{recursive:true});
await fs.copyFile(path.join(componentFixture,'convex/counter.js'),path.join(component,'counter.js'));
await fs.copyFile(path.join(componentFixture,'convex/schema.js'),path.join(component,'schema.js'));
await fs.writeFile(path.join(component,'convex.config.js'),
  'import {defineComponent} from "convex/server";export default defineComponent("probe");\n');
await fs.writeFile(path.join(project,'convex/convex.config.js'),
  'import {defineApp} from "convex/server";import probe from "../probe/convex.config.js";const app=defineApp();app.use(probe,{name:"probe"});export default app;\n');
await fs.mkdir(path.join(project,'node_modules'),{recursive:true});
await fs.symlink(path.join(root,'npm-packages/convex'),path.join(project,'node_modules/convex'));
const proofNode=process.env.CONVEX_DOTNET_PROOF_NODE||process.execPath;
const env={PATH:path.dirname(proofNode)+path.delimiter+process.env.PATH,HOME:state,LANG:'C.UTF-8',CI:'1',
  SENTRY_DSN:'',DISABLE_BEACON:'true',RUST_LOG:'warn'};
const secret=randomBytes(32).toString('hex'),deployment='native-component-proof';
let key='',backend,reactive;
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const summary={state,base:'df8338dd8974674cf0c4cfb42aff4b50f790bc9c',source:'NativeProbe.fs',checks:[]};
const log=(name,detail)=>{summary.checks.push({name,detail});console.log(`PASS ${name}`);};
const ref=name=>makeFunctionReference(`Counter:${name}`);
async function runStatus(program,args,cwd=state,extra={}){
  return await new Promise((resolve,reject)=>{
    const child=spawn(program,args,{cwd,env:{...env,...extra},stdio:['ignore','pipe','pipe']});let output='';
    for(const stream of [child.stdout,child.stderr])stream.on('data',b=>output=(output+b.toString()).slice(-1024*1024));
    child.on('error',reject);child.on('exit',code=>{
      output=output.replaceAll(key||'never-secret','[isolated-key]');
      resolve({code,output});
    });
  });
}
async function run(program,args,cwd=state,extra={}){
  const result=await runStatus(program,args,cwd,extra);
  assert.equal(result.code,0,`private command failed (${result.code}): ${result.output}`);
  return result.output;
}
async function freePort(){return await new Promise((resolve,reject)=>{
  const server=net.createServer();server.on('error',reject);server.listen(0,'127.0.0.1',()=>{
    const port=server.address().port;server.close(()=>resolve(port));
  });
});}
const port=await freePort(),sitePort=await freePort(),url=`http://127.0.0.1:${port}`;
const artifacts=await Promise.all((await fs.readdir(path.dirname(host),{withFileTypes:true})).filter(f=>f.isFile()).map(async file=>{
  const p=path.join(path.dirname(host),file.name);return {path:p,sha256:hash(await fs.readFile(p))};
}));
const manifest=path.join(state,'runtime.json');await fs.writeFile(manifest,JSON.stringify({version:1,maxWorkers:8,
  worker:{program:dotnet,programSha256:hash(await fs.readFile(dotnet)),arguments:[host],artifacts,framework:await pinnedFramework(dotnet),
    profile:'restricted-first-party',memoryMiB:256,invocationTimeoutMs:16000,maxInvocations:500},functions:[]}),{mode:0o600});
async function start(mode){
  const output=createWriteStream(path.join(state,`backend-${mode}.log`));
  backend=spawn(binary,[path.join(state,'database.sqlite3'),'--interface','127.0.0.1','--port',String(port),
    '--site-proxy-port',String(sitePort),'--instance-name',deployment,'--instance-secret',secret,
    '--local-storage',path.join(state,'storage'),'--disable-beacon'],
    {cwd:state,env:{...env,CONVEX_DOTNET_MANIFEST:manifest},stdio:['ignore','pipe','pipe']});
  backend.stdout.pipe(output);backend.stderr.pipe(output);
  for(let i=0;i<150;i++){
    if(backend.exitCode!==null)throw new Error(`private backend exited; ${state}`);
    try{if((await fetch(url+'/version',{signal:AbortSignal.timeout(500)})).ok)return;}catch{}
    await delay(100);
  }throw new Error('private component backend readiness timeout');
}
async function stop(){
  if(!backend||backend.exitCode!==null)return;
  const child=backend,finished=new Promise(resolve=>child.once('exit',resolve));child.kill('SIGTERM');
  await Promise.race([finished,delay(5000)]);if(child.exitCode===null){child.kill('SIGKILL');await finished;}backend=undefined;
  const executable=await fs.realpath(proofNode);
  for(const pid of await fs.readdir('/proc')){
    if(!/^\d+$/.test(pid))continue;
    try{
      if(await fs.realpath(`/proc/${pid}/exe`)!==executable)continue;
      if((await fs.readFile(`/proc/${pid}/environ`)).toString().split('\0').includes(`HOME=${state}`))process.kill(Number(pid),'SIGTERM');
    }catch(error){if(!['ENOENT','ESRCH','EACCES','EPERM'].includes(error.code))throw error;}
  }
}
async function owner(route,body,expectedCode){
  const response=await fetch(url+route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),
    signal:AbortSignal.timeout(30000)});
  const value=await response.json();
  if(expectedCode){assert.equal(response.status,400);assert.equal(value.code,expectedCode);return value;}
  assert.ok(response.ok,`owner ${route} refused: ${JSON.stringify(value).replaceAll(key,'[isolated-key]')}`);return value;
}
async function capsule(file){return {path:'convex.config.js',source:await fs.readFile(path.join(capsules,file),'utf8'),sourceMap:null,environment:'dotNet'};}
async function waitSchema(startPush){
  for(let i=0;i<60;i++){
    const result=await owner('/api/deploy2/wait_for_schema',{adminKey:key,schemaChange:startPush.schemaChange,timeoutMs:1000,dryRun:false});
    if(result.type==='complete')return;
    assert.equal(result.type,'inProgress');
  }throw new Error('owning component schema activation timeout');
}
async function activate(graph){
  const file=path.join(state,`graph-${summary.checks.length}.json`);await fs.writeFile(file,JSON.stringify(graph),{mode:0o600});
  const outcome=JSON.parse((await run(dotnet,[nativeCli,'deploy-graph',url,file,'PROOF_NATIVE_ADMIN_KEY'],state,
    {PROOF_NATIVE_ADMIN_KEY:key,CONVEX_DOTNET_STATE_DIRECTORY:path.join(state,'native-client-journal')})).trim());
  assert.equal(outcome.outcome,'confirmed');
}
async function receiptClientProof(current,baseline,admin){
  const requests=[],sockets=new Set();
  let dropFinish=true,readMode='owner',preparedReceipt,committedReceipt;
  const proxy=httpServer(async(request,response)=>{
    requests.push(request.url);
    try{
      const chunks=[];for await(const chunk of request)chunks.push(chunk);
      if(request.url==='/api/deploy2/native_receipt'&&readMode!=='owner'){
        const value=readMode==='absent'?null:readMode==='mismatch'?{...committedReceipt,startPushSha256:'0'.repeat(64)}:
          readMode==='numericTimestamp'?{...committedReceipt,commitTimestamp:1}:{};
        response.writeHead(readMode==='error'?503:200,{'Content-Type':'application/json'});
        response.end(JSON.stringify(value));return;
      }
      const upstream=await fetch(url+request.url,{method:request.method,headers:{'Content-Type':'application/json'},
        body:Buffer.concat(chunks),signal:AbortSignal.timeout(90000)});
      const bytes=Buffer.from(await upstream.arrayBuffer());
      if(request.url==='/api/deploy2/start_push'&&upstream.ok)preparedReceipt=JSON.parse(bytes).nativeReceipt;
      if(request.url==='/api/deploy2/finish_push'&&dropFinish){
        assert.equal(upstream.status,200,'discard only a confirmed owning activation reply');
        dropFinish=false;response.destroy();return;
      }
      response.writeHead(upstream.status,{'Content-Type':upstream.headers.get('Content-Type')??'application/json'});response.end(bytes);
    }catch(error){response.destroy(error);}
  });
  proxy.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
  await new Promise(resolve=>proxy.listen(0,'127.0.0.1',resolve));
  const origin=`http://127.0.0.1:${proxy.address().port}`,journal=path.join(state,'receipt-client-journal');
  const graphPath=path.join(state,'receipt-native-graph.json'),laterPath=path.join(state,'receipt-later-graph.json');
  await fs.writeFile(graphPath,JSON.stringify(current),{mode:0o600});await fs.writeFile(laterPath,JSON.stringify(baseline),{mode:0o600});
  const command=async args=>{
    const result=await runStatus(dotnet,[receiptCli,...args,'PROOF_NATIVE_ADMIN_KEY'],state,
      {PROOF_NATIVE_ADMIN_KEY:key,CONVEX_DOTNET_STATE_DIRECTORY:journal});
    return {...result,value:JSON.parse(result.output.trim())};
  };
  const deploy=file=>command(['deploy-graph',origin,file]);
  const reconcile=()=>command(['reconcile',origin]);
  try{
    const lost=await deploy(graphPath);assert.equal(lost.code,3,lost.output);assert.equal(lost.value.outcome,'uncertain');
    assert.equal(requests.filter(route=>route==='/api/deploy2/finish_push').length,1);
    assert.ok(preparedReceipt,'owner preparation token must precede finish delivery');
    committedReceipt=await owner('/api/deploy2/native_receipt',{adminKey:key,operationId:preparedReceipt.operationId});
    assert.equal(committedReceipt.outcome,'committed');assert.equal(committedReceipt.startPushSha256,preparedReceipt.startPushSha256);
    assert.equal(await admin.function('counter:environment','probe',{}),'owning-component-value');
    for(const file of [graphPath,laterPath]){
      const before=requests.length,repeated=await deploy(file);assert.equal(repeated.code,3,repeated.output);
      assert.equal(repeated.value.sent,false);assert.equal(requests.length,before,'unknown full graph must fence every later intent');
    }
    log('receipt CLI loses one confirmed finish reply, retains owner token and sends zero same/different intent redeliveries');
    for(readMode of ['absent','mismatch','numericTimestamp','error']){
      const before=requests.length,result=await reconcile();assert.equal(result.code,3,result.output);
      assert.equal(result.value.outcome,'uncertain');assert.equal(result.value.sent,false);
      assert.deepEqual(requests.slice(before),['/api/deploy2/native_receipt']);
      const fenced=requests.length,repeated=await deploy(laterPath);assert.equal(repeated.code,3,repeated.output);
      assert.equal(repeated.value.sent,false);assert.equal(requests.length,fenced);
    }
    log('absent/mismatched/numeric/error receipt replies preserve uncertainty and the origin delivery fence');
    // A distinct administrator can change current state while the immutable
    // receipt continues to prove the earlier activation. Reconciliation must
    // not pretend that the earlier graph is still active.
    const superseding=await owner('/api/deploy2/start_push',{...baseline,adminKey:key});await waitSchema(superseding);
    await owner('/api/deploy2/finish_push',{adminKey:key,startPush:superseding,dryRun:false,message:null});
    assert.equal(await admin.function('counter:environment','probe',{}),null);
    assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId:committedReceipt.operationId}),committedReceipt);
    readMode='owner';const before=requests.length,settled=await reconcile();assert.equal(settled.code,0,settled.output);
    assert.equal(settled.value.outcome,'confirmed');assert.equal(settled.value.receipt,'historicalOwningCommit');assert.equal(settled.value.sent,false);
    assert.equal(settled.value.operationId,committedReceipt.operationId);assert.equal(settled.value.startPushSha256,committedReceipt.startPushSha256);
    assert.equal(settled.value.commitTimestamp,committedReceipt.commitTimestamp);assert.deepEqual(requests.slice(before),['/api/deploy2/native_receipt']);
    const recordedAt=requests.length,recorded=await deploy(graphPath);assert.equal(recorded.code,0,recorded.output);
    assert.equal(recorded.value.outcome,'recorded');assert.equal(requests.length,recordedAt);
    assert.equal(await admin.function('counter:environment','probe',{}),null);
    log('explicit owner read settles the historical commit without claiming or reactivating the superseded graph',committedReceipt);
    const laterStart=requests.length,later=await deploy(laterPath);assert.equal(later.code,0,later.output);assert.equal(later.value.outcome,'confirmed');
    assert.equal(requests.slice(laterStart).filter(route=>route==='/api/deploy2/finish_push').length,1);
    assert.notEqual(preparedReceipt.operationId,committedReceipt.operationId);
    const laterReceipt=await owner('/api/deploy2/native_receipt',{adminKey:key,operationId:preparedReceipt.operationId});
    assert.equal(laterReceipt.outcome,'committed');assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId:committedReceipt.operationId}),committedReceipt);
    log('matching historical reconciliation permits one later distinct intent with a fresh immutable owning receipt',laterReceipt);
    const restored=await owner('/api/deploy2/start_push',{...current,adminKey:key});await waitSchema(restored);
    await owner('/api/deploy2/finish_push',{adminKey:key,startPush:restored,dryRun:false,message:null});
    assert.equal(await admin.function('counter:environment','probe',{}),'owning-component-value');
  }finally{
    for(const socket of sockets)socket.destroy();await new Promise(resolve=>proxy.close(resolve));
  }
}

try{
  summary.backendSha256=hash(await fs.readFile(binary));summary.hostArtifacts=artifacts;
  if(receiptCli)summary.receiptCliSha256=hash(await fs.readFile(receiptCli));
  summary.capsules=await Promise.all((await fs.readdir(capsules)).filter(f=>f.endsWith('.js')).map(async file=>({path:file,sha256:hash(await fs.readFile(path.join(capsules,file)))})));
  key=(await run(binary,['keygen','admin-key','--instance-name',deployment,'--instance-secret',secret])).trim();await start('baseline');
  const cli=path.join(root,'npm-packages/convex/bin/main.js');
  const deployArgs=['deploy','--url',url,'--admin-key',key,'--typecheck','disable','--codegen','disable','--skip-workos-check'];
  await run(process.execPath,[cli,...deployArgs],project);
  const client=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  const admin=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});admin.setAdminAuth(key);
  const componentId=(await admin.query(makeFunctionReference('_system/frontend/components:list'),{})).find(component=>component.path==='probe').id;
  const componentRows=async()=>await admin.query(makeFunctionReference('_system/frontend/listTableScan:default'),
    {table:'counters',limit:100,componentId});
  const componentSchema=async()=>{
    const value=await admin.query(makeFunctionReference('_system/frontend/getSchemas:default'),{componentId});
    assert.equal(value.inProgress,undefined);return JSON.parse(value.active);
  };
  const originalSchema=await componentSchema();
  assert.deepEqual(await client.mutation(ref('componentIncrement'),{name:'retained',amount:1}),{name:'retained',value:1});
  const retainedId=(await componentRows()).find(row=>row.name==='retained')._id;
  assert.equal(await client.query(ref('read'),{name:'retained'}),null);
  await assert.rejects(client.function('counter:read','probe',{name:'retained'}));
  log('same F# child V8 execution, namespace isolation, stored ID and direct component authority refusal');
  const compiled=path.join(state,'sdk-prepared-graph');await run(process.execPath,[cli,...deployArgs,'--write-push-request',compiled],project);
  const baseline=JSON.parse(await fs.readFile(compiled+'.json','utf8'));delete baseline.adminKey;
  await fs.writeFile(compiled+'.json',JSON.stringify(baseline),{mode:0o600});
  assert.deepEqual(baseline.appDefinition.dependencies,['../probe']);assert.equal(baseline.componentDefinitions[0].definitionPath,'../probe');
  let current;
  for(const mode of ['native-app','native-child','native-app-and-child']){
    const graph=structuredClone(baseline);
    if(mode!=='native-child')graph.appDefinition.definition=await capsule('app.js');
    if(mode!=='native-app'){
      const child=graph.componentDefinitions[0];child.definition=await capsule('child.js');
      child.schema={path:'schema.js',source:await fs.readFile(path.join(capsules,'schema.js'),'utf8'),sourceMap:null,environment:'dotNet'};
      child.functions=[{path:'counter.js',source:await fs.readFile(path.join(capsules,'counter.js'),'utf8'),sourceMap:null,environment:'dotNet'}];
    }
    await activate(graph);current=graph;
    assert.deepEqual(await componentSchema(),originalSchema);
    assert.equal((await componentRows()).find(row=>row.name==='retained')._id,retainedId);
    assert.deepEqual(await client.mutation(ref('componentIncrement'),{name:mode,amount:2}),{name:mode,value:2});
    assert.deepEqual(await client.query(ref('componentRead'),{name:mode}),{name:mode,value:2});
    assert.equal(await client.query(ref('read'),{name:mode}),null);
    await assert.rejects(client.function('counter:read','probe',{name:mode}));
    log(`${mode}:owning mixed DAG admission, native component execution and unchanged schema/IDs/authority`);
  }
  reactive=new ConvexClient(url,{logger:false});const observed=[];
  const unsubscribe=reactive.onUpdate(ref('componentRead'),{name:'reactive'},value=>observed.push(value));
  const wait=async predicate=>{for(let i=0;i<150;i++){if(observed.some(predicate))return;await delay(100);}throw new Error('native component dependency did not invalidate root query');};
  await wait(value=>value===null);await client.mutation(ref('componentIncrement'),{name:'reactive',amount:1});
  await wait(value=>value?.value===1);unsubscribe();await reactive.close();reactive=undefined;
  log('native child index-range entrant invalidates the owning V8 parent WebSocket subscription');
  const changes=await fetch(url+'/api/update_environment_variables',{method:'POST',headers:{'Content-Type':'application/json',Authorization:`Convex ${key}`},
    body:JSON.stringify({changes:[{name:'DBM_NATIVE_COMPONENT_PROBE',value:'owning-component-value'}]}),signal:AbortSignal.timeout(10000)});assert.ok(changes.ok);
  const bound=structuredClone(current);bound.appDefinition.definition=await capsule('app-environment.js');
  bound.componentDefinitions[0].definition=await capsule('child-declared-environment.js');await activate(bound);current=bound;
  assert.equal(await admin.function('counter:environment','probe',{}),'owning-component-value');
  log('native app reads owning deployment environment and binds a declared child environment');
  for(const [file,code,selected] of [['app-clock.js','NoDateDuringDefinitionEvaluation','app'],
    ['app-random.js','NoRandomDuringDefinitionEvaluation','app'],['child-environment.js','EnvironmentVariablesUnsupported','child']]){
    const invalid=structuredClone(current);
    if(selected==='app')invalid.appDefinition.definition=await capsule(file);else invalid.componentDefinitions[0].definition=await capsule(file);
    await owner('/api/deploy2/start_push',{...invalid,adminKey:key},code);
    assert.equal(await admin.function('counter:environment','probe',{}),'owning-component-value');
    log(`native declaration preserves owning ${code} refusal without activation`);
  }
  const cyclic=structuredClone(current);cyclic.componentDefinitions[0].dependencies=['../probe'];
  await owner('/api/deploy2/start_push',{...cyclic,adminKey:key},'CyclicImport');
  log('native definitions retain owning cyclic graph refusal');

  const operationId=randomBytes(16).toString('hex');
  const prepared=await owner('/api/deploy2/start_push',{...current,adminKey:key,nativeDeploymentId:operationId});
  assert.equal(prepared.nativeReceipt.operationId,operationId);assert.match(prepared.nativeReceipt.startPushSha256,/^[0-9a-f]{64}$/);
  await waitSchema(prepared);
  assert.equal(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),null);
  const tampered=structuredClone(prepared);tampered.environmentVariables.TAMPER='changed';
  await owner('/api/deploy2/finish_push',{adminKey:key,startPush:tampered,dryRun:false,message:null},'NativeDeploymentReceiptMismatch');
  assert.equal(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),null);
  await owner('/api/deploy2/finish_push',{adminKey:key,startPush:prepared,dryRun:false,message:null});
  const receipt=await owner('/api/deploy2/native_receipt',{adminKey:key,operationId});
  assert.equal(receipt.outcome,'committed');assert.deepEqual({operationId:receipt.operationId,startPushSha256:receipt.startPushSha256},prepared.nativeReceipt);
  assert.equal(typeof receipt.commitTimestamp,'string');assert.match(receipt.commitTimestamp,/^\d{1,20}$/);
  await owner('/api/deploy2/finish_push',{adminKey:key,startPush:prepared,dryRun:false,message:null},'DeploymentAlreadyCommitted');
  assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),receipt);
  const replacement=await owner('/api/deploy2/start_push',{...current,adminKey:key,nativeDeploymentId:operationId});await waitSchema(replacement);
  assert.notEqual(replacement.nativeReceipt.startPushSha256,receipt.startPushSha256);
  await owner('/api/deploy2/finish_push',{adminKey:key,startPush:replacement,dryRun:false,message:null},'NativeDeploymentReceiptConflict');
  assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),receipt);
  assert.equal((await componentRows()).find(row=>row.name==='retained')._id,retainedId);
  log('full-graph owning receipt is atomic, immutable and refuses altered snapshots and repeated activation',receipt);
  const denied=await fetch(url+'/api/deploy2/native_receipt',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({adminKey:'unauthorized',operationId})});assert.ok(!denied.ok);
  await owner('/api/deploy2/native_receipt',{adminKey:key,operationId:'invalid'},'InvalidNativeDeploymentReceipt');
  log('owning deployment receipts require deploy authority and exact bounded operation IDs');
  if(receiptCli)await receiptClientProof(current,baseline,admin);
  await stop();await start('restart');assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),receipt);
  assert.deepEqual(await componentSchema(),originalSchema);assert.equal((await componentRows()).find(row=>row.name==='retained')._id,retainedId);
  assert.deepEqual(await client.query(ref('componentRead'),{name:'native-app-and-child'}),{name:'native-app-and-child',value:2});
  assert.equal(await admin.function('counter:environment','probe',{}),'owning-component-value');
  log('native component namespace/artifacts/environment and immutable activation receipt survive cold restart');
  await run(process.execPath,[cli,...deployArgs],project);
  assert.deepEqual(await componentSchema(),originalSchema);assert.equal((await componentRows()).find(row=>row.name==='retained')._id,retainedId);
  assert.deepEqual(await client.query(ref('componentRead'),{name:'native-app-and-child'}),{name:'native-app-and-child',value:2});
  assert.deepEqual(await owner('/api/deploy2/native_receipt',{adminKey:key,operationId}),receipt);
  log('owning Fable app/component rollback preserves native-written IDs/data and historical receipt');
  console.log(`Actual native component/receipt proof passed. Evidence: ${path.join(state,'summary.json')}`);
}finally{
  if(reactive)await reactive.close();await stop();await fs.writeFile(path.join(state,'summary.json'),JSON.stringify(summary,null,2));
}
