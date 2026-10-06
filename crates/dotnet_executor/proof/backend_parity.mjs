// Actual patched-backend proof. Every process, port, key and data file belongs
// to a newly-created temporary directory; existing deployments are never used.
import assert from 'node:assert/strict';
import {pinnedFramework} from './pinned_framework.mjs';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import net from 'node:net';
import httpServer from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const root=fileURLToPath(new URL('../../..',import.meta.url));
const [dotnet,host,assembly,fixture,nativeCapsules,nativeCli]=process.argv.slice(2);
assert.ok([dotnet,host,assembly,fixture,nativeCapsules,nativeCli].every(p=>p&&path.isAbsolute(p)),
  'usage: backend_parity.mjs /absolute/dotnet /absolute/Host.dll /absolute/RuntimeFixture.dll /absolute/parity /absolute/native-capsules /absolute/Cli.dll');
const binary=path.join(root,'target/debug/convex-local-backend');
const {ConvexHttpClient,ConvexClient}=await import(path.join(root,'npm-packages/convex/dist/esm/browser/index.js'));
const {makeFunctionReference}=await import(path.join(root,'npm-packages/convex/dist/esm/server/index.js'));
const {convexToJson}=await import(path.join(root,'npm-packages/convex/dist/esm/values/index.js'));
const state=await fs.mkdtemp('/tmp/convex-dotnet-backend-parity-');
await fs.chmod(state,0o700);
const project=path.join(state,'project');
await fs.cp(fixture,project,{recursive:true});
await fs.writeFile(path.join(project,'convex/crons.js'),'export {crons as default} from "./Counter.js";\n');
await fs.mkdir(path.join(project,'node_modules'),{recursive:true});
await fs.symlink(path.join(root,'npm-packages/convex'),path.join(project,'node_modules/convex'));
// Real SDK component declarations remain JavaScript. The native root calls
// their exported references through the backend's existing component owner.
const component=path.join(project,'probe');
await fs.mkdir(path.join(component,'_generated'),{recursive:true});
await fs.writeFile(path.join(project,'convex/convex.config.js'),`import {defineApp} from "convex/server";
import probe from "../probe/convex.config.js";
const app=defineApp();app.use(probe,{name:"probe"});export default app;\n`);
await fs.writeFile(path.join(component,'convex.config.js'),`import {defineComponent} from "convex/server";
export default defineComponent("probe");\n`);
await fs.writeFile(path.join(component,'schema.js'),`import {defineSchema,defineTable} from "convex/server";
import {v} from "convex/values";
export default defineSchema({counters:defineTable({name:v.string(),value:v.number()}).index("by_name",["name"])});\n`);
await fs.writeFile(path.join(component,'_generated/server.js'),`// SDK-generated bindings, specialized only by the component's data model.
export {queryGeneric as query,mutationGeneric as mutation} from "convex/server";\n`);
await fs.writeFile(path.join(component,'counter.js'),`import {query,mutation} from "./_generated/server.js";
import {v} from "convex/values";
const document=v.object({name:v.string(),value:v.number()});
export const read=query({args:{name:v.string()},returns:v.union(v.null(),document),handler:async(ctx,args)=>{
 const row=await ctx.db.query("counters").withIndex("by_name",q=>q.eq("name",args.name)).unique();
 return row?{name:row.name,value:row.value}:null;
}});
export const increment=mutation({args:{name:v.string(),amount:v.number()},returns:document,handler:async(ctx,args)=>{
 const row=await ctx.db.query("counters").withIndex("by_name",q=>q.eq("name",args.name)).unique();
 const value=(row?.value??0)+args.amount;
 if(row)await ctx.db.patch(row._id,{value});else await ctx.db.insert("counters",{name:args.name,value});
 return {name:args.name,value};
}});\n`);
const secret=randomBytes(32).toString('hex');
const deployment='native-parity-proof';
const proofNode=process.env.CONVEX_DOTNET_PROOF_NODE||process.execPath;
assert.ok(path.isAbsolute(proofNode),'proof Node executable must be absolute');
const cleanEnv={PATH:path.dirname(proofNode)+path.delimiter+process.env.PATH,HOME:state,LANG:'C.UTF-8',CI:'1',SENTRY_DSN:'',DISABLE_BEACON:'true',
  RUST_LOG:'warn',DISABLE_METRICS_ENDPOINT:'false'};
let key='';
let backend;
let reactive;
const summary={source:'Counter.fs',base:'df8338dd8974674cf0c4cfb42aff4b50f790bc9c',state,checks:[]};
const log=(name,detail)=>{summary.checks.push({name,detail});console.log(`PASS ${name}`);};
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const ref=name=>makeFunctionReference(`Counter:${name}`);
const canonical=value=>convexToJson(value);

async function runStatus(program,args,cwd=state,extraEnvironment={}){
  return await new Promise((resolve,reject)=>{
    const child=spawn(program,args,{cwd,env:{...cleanEnv,...extraEnvironment},stdio:['ignore','pipe','pipe']});
    let output='';
    for(const stream of [child.stdout,child.stderr])stream.on('data',b=>{output=(output+b.toString()).slice(-1024*1024);});
    child.on('error',reject);
    child.on('exit',code=>resolve({code,output:output.replaceAll(key||'never-secret','[isolated-key]')}));
  });
}
async function run(program,args,cwd=state,extraEnvironment={}){
  const result=await runStatus(program,args,cwd,extraEnvironment);
  assert.equal(result.code,0,`command failed (${result.code}): ${result.output}`);
  return result.output;
}
async function freePort(){
  return await new Promise((resolve,reject)=>{
    const server=net.createServer();server.on('error',reject);
    server.listen(0,'127.0.0.1',()=>{const port=server.address().port;server.close(()=>resolve(port));});
  });
}
const port=await freePort(),sitePort=await freePort();
const url=`http://127.0.0.1:${port}`;
const siteUrl=`http://127.0.0.1:${sitePort}`;
async function start(mode,manifest){
  const output=createWriteStream(path.join(state,`backend-${mode}.log`),{flags:'a'});
  backend=spawn(binary,[path.join(state,'database.sqlite3'),'--interface','127.0.0.1','--port',String(port),
    '--site-proxy-port',String(sitePort),'--instance-name',deployment,'--instance-secret',secret,
    '--local-storage',path.join(state,'storage'),'--disable-beacon'],
    {cwd:state,env:{...cleanEnv,...(manifest?{CONVEX_DOTNET_MANIFEST:manifest}:{})},stdio:['ignore','pipe','pipe']});
  backend.stdout.pipe(output);backend.stderr.pipe(output);
  for(let i=0;i<150;i++){
    if(backend.exitCode!==null)throw new Error(`isolated backend exited (${backend.exitCode}); ${state}`);
    try{if((await fetch(url+'/version',{signal:AbortSignal.timeout(500)})).ok)return;}catch{}
    await delay(100);
  }
  throw new Error(`isolated backend startup timeout; ${state}`);
}
async function stop(){
  if(!backend||backend.exitCode!==null)return;
  const child=backend;
  const finished=new Promise(resolve=>child.once('exit',resolve));
  child.kill('SIGTERM');
  await Promise.race([finished,delay(5000)]);
  if(child.exitCode===null){child.kill('SIGKILL');await finished;}
  backend=undefined;
  // The owning local Node executor can leave grandchildren with inherited
  // stdout after its backend exits. Retire only Node processes whose exact
  // private HOME proves they belong to this synthetic run, never a user Node
  // service or another proof's process.
  const executable=await fs.realpath(proofNode);
  for(const pid of await fs.readdir('/proc')){
    if(!/^\d+$/.test(pid))continue;
    try{
      if(await fs.realpath(`/proc/${pid}/exe`)!==executable)continue;
      const inherited=(await fs.readFile(`/proc/${pid}/environ`)).toString().split('\0');
      if(inherited.includes(`HOME=${state}`))process.kill(Number(pid),'SIGTERM');
    }catch(error){if(!['ENOENT','ESRCH','EACCES','EPERM'].includes(error.code))throw error;}
  }
}
async function post(route,body){
  const response=await fetch(url+route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(10000)});
  assert.ok(response.ok,`isolated admin route ${route} failed: ${await response.clone().text()}`);
  return await response.json();
}
async function nativeDeploy(origin,graphPath){
  return await runStatus(dotnet,[nativeCli,'deploy-graph',origin,graphPath,'PROOF_NATIVE_ADMIN_KEY'],
    state,{PROOF_NATIVE_ADMIN_KEY:key,CONVEX_DOTNET_STATE_DIRECTORY:path.join(state,'native-client-journal')});
}
async function lostFinishReply(graphPath){
  const requests=[];
  const sockets=new Set();
  let owningFinishConfirmed=false;
  const proxy=httpServer.createServer(async(request,response)=>{
    requests.push(request.url);
    try{
      const chunks=[];
      for await(const chunk of request)chunks.push(chunk);
      const upstream=await fetch(url+request.url,{method:request.method,
        headers:{'Content-Type':'application/json'},body:Buffer.concat(chunks),signal:AbortSignal.timeout(90000)});
      const bytes=Buffer.from(await upstream.arrayBuffer());
      if(request.url==='/api/deploy2/finish_push'){
        assert.equal(upstream.status,200,'private proxy must drop only a confirmed owner finish response');
        owningFinishConfirmed=true;
        response.destroy();return;
      }
      response.writeHead(upstream.status,{'Content-Type':upstream.headers.get('Content-Type')??'application/json'});
      response.end(bytes);
    }catch(error){response.destroy(error);}
  });
  proxy.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
  await new Promise(resolve=>proxy.listen(0,'127.0.0.1',resolve));
  const origin=`http://127.0.0.1:${proxy.address().port}`;
  try{
    const first=await nativeDeploy(origin,graphPath);
    assert.equal(first.code,3,first.output);
    const failure=JSON.parse(first.output.trim());
    assert.equal(failure.outcome,'uncertain');assert.equal(failure.redelivery,'readOwningDeploymentState');
    assert.equal(requests.filter(route=>route==='/api/deploy2/finish_push').length,1);
    assert.equal(owningFinishConfirmed,true,'the owner did not confirm the finish that lost its reply');
    const owning=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
    assert.equal(owning.find(module=>module.path==='AliasCounter.js').environment,'dotNet');
    const observed=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
    assert.deepEqual(await observed.query(aliasRef('read'),{name:'alias-shared'}),{name:'alias-shared',value:10});
    const count=requests.length;
    const repeated=await nativeDeploy(origin,graphPath);
    assert.equal(repeated.code,3,repeated.output);assert.equal(JSON.parse(repeated.output.trim()).sent,false);
    assert.equal(requests.length,count,'an uncertain activation must not send again before reconciliation');
    log('native deployment CLI records lost finish reply, owner readback and zero automatic redelivery',
      {ownerFinishResponses:1,additionalRequests:requests.length-count});
  }finally{
    for(const socket of sockets)socket.destroy();
    await new Promise(resolve=>proxy.close(resolve));
  }
}
async function docs(){
  const client=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true});client.setAdminAuth(key);
  const result=await client.query(makeFunctionReference('_system/cli/tableData:default'),
    {table:'runtimeCounters',order:'asc',paginationOpts:{numItems:100,cursor:null}});
  return result.page;
}
async function changeEnvironment(changes){
  const response=await fetch(url+'/api/update_environment_variables',{method:'POST',
    headers:{'Content-Type':'application/json',Authorization:`Convex ${key}`},
    body:JSON.stringify({changes}),signal:AbortSignal.timeout(10000)});
  assert.ok(response.ok,`isolated environment update failed: ${await response.clone().text()}`);
}
async function http(client,mode){
  const bytes=new Uint8Array(150*1024+17);for(let i=0;i<bytes.length;i++)bytes[i]=i%256;
  const echo=await fetch(siteUrl+'/native/echo',{method:'POST',headers:{'x-native-probe':'body-probe'},body:bytes,
    signal:AbortSignal.timeout(20000)});
  assert.equal(echo.status,200);assert.equal(echo.headers.get('x-native-probe'),'body-probe');
  assert.deepEqual(new Uint8Array(await echo.arrayBuffer()),bytes);
  for(const method of ['GET','HEAD']){
    const prefix=await fetch(siteUrl+'/native/prefix/child?query=preserved',{method,headers:{'x-native-probe':'prefix-probe'},
      signal:AbortSignal.timeout(10000)});
    assert.equal(prefix.status,200);assert.equal(prefix.headers.get('x-native-probe'),'prefix-probe');
    assert.equal((await prefix.arrayBuffer()).byteLength,0);
  }
  assert.equal((await fetch(siteUrl+'/native/missing',{signal:AbortSignal.timeout(10000)})).status,404);
  const name=`http-${mode}`;
  const mutation=await fetch(siteUrl+'/native/mutation',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({name,amount:6}),signal:AbortSignal.timeout(10000)});
  assert.equal(mutation.status,200);assert.deepEqual(await mutation.json(),{name,value:6});
  assert.deepEqual(await client.query(ref('read'),{name}),{name,value:6});
  log(`${mode}:HTTP exact/prefix/HEAD binary clone and owning mutation callback`);
}
async function common(client,mode){
  const value={integer:9223372036854775807n,float:Infinity,bytes:new Uint8Array([0,1,255]).buffer,empty:null,zero:-0};
  assert.deepEqual(canonical(await client.query(ref('echo'),{value})),canonical(value));log(`${mode}:canonical scalars`);
  assert.equal(await client.query(ref('identity'),{}),null);
  await assert.rejects(client.mutation(ref('authorizedIncrement'),{name:`unauthorized-${mode}`,amount:1}));
  assert.equal(await client.query(ref('read'),{name:`unauthorized-${mode}`}),null);log(`${mode}:auth refusal without write`);
  const authenticated=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  authenticated.setAdminAuth(key,{issuer:'https://synthetic-proof.invalid',subject:`alice-${mode}`});
  assert.equal(await authenticated.query(ref('identity'),{}),`alice-${mode}`);
  assert.deepEqual(await authenticated.mutation(ref('authorizedIncrement'),{name:`authorized-${mode}`,amount:4}),
    {name:`authorized-${mode}`,value:4});
  assert.equal(await client.query(ref('identity'),{}),null);
  authenticated.setAdminAuth(key,{issuer:'https://synthetic-proof.invalid',subject:`bob-${mode}`});
  assert.equal(await authenticated.query(ref('identity'),{}),`bob-${mode}`);
  log(`${mode}:backend-owned identity and worker reuse isolation`);
  await assert.rejects(client.mutation(ref('fail'),{name:`rollback-${mode}`}),error=>{
    assert.deepEqual(error.data,{operation:`rollback-${mode}`,reason:'refused'});return true;
  });
  assert.equal(await client.query(ref('read'),{name:`rollback-${mode}`}),null);log(`${mode}:structured error and rollback`);
  await assert.rejects(client.mutation(ref('invalidDocument'),{name:`invalid-document-${mode}`}));
  assert.equal(await client.query(ref('read'),{name:`invalid-document-${mode}`}),null);
  log(`${mode}:owning schema refuses invalid raw document and rolls back`);
  const nestedRollbackName=`nested-rollback-${mode}`;
  assert.deepEqual(await client.mutation(ref('nestedRollback'),{name:nestedRollbackName}),
    {operation:nestedRollbackName,reason:'refused'});
  assert.deepEqual(await client.query(ref('read'),{name:nestedRollbackName}),{name:nestedRollbackName,value:1});
  log(`${mode}:caught nested error rolls back child and commits parent`);
  const clock=await client.query(ref('clock'),{});assert.equal(clock[0],clock[1]);assert.ok(clock.every(Number.isInteger));
  const random=await client.query(ref('randomValue'),{});assert.ok(random>=0&&random<1);log(`${mode}:controlled clock and RNG`);
  assert.equal(await client.query(ref('environmentValue'),{name:'PROOF_ENV'}),'deployment-value');
  assert.equal(await client.query(ref('environmentValue'),{name:'HOME'}),null);
  assert.equal(await client.query(ref('environmentValue'),{name:'CONVEX_DOTNET_MANIFEST'}),null);
  await assert.rejects(client.query(ref('environmentValue'),{name:'invalid-name'}));
  await assert.rejects(client.query(ref('environmentValue'),{name:'X'.repeat(257)}));
  log(`${mode}:owning environment value, invalid name and ambient secret refusal`);
  const expected=await client.query(ref('read'),{name:'shared'});
  assert.deepEqual(await client.query(ref('nestedRead'),{name:'shared'}),expected);log(`${mode}:nested query`);
  const nested=await client.mutation(ref('nestedIncrement'),{name:`nested-${mode}`,amount:2});assert.deepEqual(nested,{name:`nested-${mode}`,value:2});
  const action=await client.action(ref('incrementAction'),{name:`action-${mode}`,amount:3});assert.deepEqual(action,{name:`action-${mode}`,value:3});
  log(`${mode}:nested mutation and action transaction`);
  const stored=await client.action(ref('storageRoundtrip'),{value:new Uint8Array([1,2,3,255]).buffer});
  assert.deepEqual(canonical(stored.bytes),canonical(new Uint8Array([1,2,3,255]).buffer));
  assert.equal(stored.contentType,'application/octet-stream');log(`${mode}:owning binary storage roundtrip`);
  await http(client,mode);
  return await client.query(ref('handle'),{});
}
async function capabilities(client,mode){
  for(const operation of ['commitWrite','commitNested']){
    const name=`commit-${mode}-${operation}`;
    const timestamp=await client.mutation(ref(operation),{name});
    assert.equal(typeof timestamp,'bigint');assert.ok(timestamp>0n);
    assert.deepEqual(await client.query(ref('commitRead'),{name}),{name,committedAt:timestamp});
  }
  await assert.rejects(client.query(ref('commitVariable'),{}),/commit/i);
  log(`${mode}:owning commit timestamp settlement, nested pending args/return and query refusal`);
  const category=`search-${mode}`;
  const rows=[
    {name:`${category}-first`,category,text:'alpha first',embedding:[1,0,0,0]},
    {name:`${category}-second`,category,text:'alpha second',embedding:[0,1,0,0]},
    {name:`${category}-excluded`,category:category+'-other',text:'alpha excluded',embedding:[1,0,0,0]}];
  const ids=[];
  for(const row of rows)ids.push(await client.mutation(ref('insertSearch'),row));
  const ownAdmin=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});ownAdmin.setAdminAuth(key);
  const metadata=await ownAdmin.query(makeFunctionReference('_system/cli/modules:apiSpec'),{});
  const byId=metadata.find(spec=>spec.identifier==='Counter.js:readById');assert.ok(byId);
  assert.deepEqual(byId.args,{type:'object',value:{id:{fieldType:{type:'id',tableName:'runtimeCounters'},optional:false}}});
  const retainedCounter=(await docs()).find(doc=>doc.name==='shared');assert.ok(retainedCounter);
  assert.deepEqual(await client.query(ref('readById'),{id:retainedCounter._id}),{name:'shared',value:retainedCounter.value});
  await assert.rejects(client.query(ref('readById'),{id:ids[0]}));
  log(`${mode}:owning opaque table-ID argument metadata, retained ID and foreign table refusal`);
  assert.deepEqual((await client.query(ref('search'),{category,query:'alpha'})).sort(),rows.slice(0,2).map(row=>row.name).sort());
  const hits=await client.action(ref('vectors'),{category,vector:[1,0,0,0]});
  assert.equal(hits.length,2);assert.deepEqual(new Set(hits.map(hit=>hit.id)),new Set(ids.slice(0,2)));
  assert.ok(Math.abs(hits.find(hit=>hit.id===ids[0]).score-1)<1e-6);
  assert.ok(Math.abs(hits.find(hit=>hit.id===ids[1]).score)<1e-6);
  await assert.rejects(client.action(ref('vectors'),{category,vector:[1,0,0]}));
  log(`${mode}:owning text/vector indexes, filter isolation and dimension refusal`);
  const failedName=`schedule-failed-${mode}`;
  let refusedId;
  await assert.rejects(client.mutation(ref('scheduleFail'),{name:failedName,amount:7}),error=>{
    assert.equal(error.data.operation,failedName);assert.equal(error.data.reason,'schedule-refused');
    refusedId=error.data.scheduledId;assert.equal(typeof refusedId,'string');return true;
  });
  assert.equal(await client.query(ref('scheduledState'),{id:refusedId}),null);
  assert.equal(await client.query(ref('read'),{name:failedName}),null);
  const canceledName=`schedule-canceled-${mode}`;
  const canceled=await client.mutation(ref('scheduleCancel'),{name:canceledName,amount:7});
  assert.equal(await client.query(ref('scheduledState'),{id:canceled}),'canceled');
  assert.equal(await client.query(ref('read'),{name:canceledName}),null);
  const dispatchedName=`schedule-dispatched-${mode}`;
  const dispatched=await client.mutation(ref('schedule'),{name:dispatchedName,amount:7});
  let finished=false;
  for(let i=0;i<150;i++){
    const state=await client.query(ref('scheduledState'),{id:dispatched});
    assert.notEqual(state,'failed','owning scheduled callback failed');
    if(state==='success'){finished=true;break;}await delay(100);
  }
  assert.ok(finished,'owning scheduled callback did not complete within15seconds');
  assert.deepEqual(await client.query(ref('read'),{name:dispatchedName}),{name:dispatchedName,value:7});
  log(`${mode}:scheduler rollback, cancellation and durable-alias callback dispatch`);
  const componentName=`component-${mode}`;
  assert.equal(await client.query(ref('componentRead'),{name:componentName}),null);
  assert.deepEqual(await client.mutation(ref('componentIncrement'),{name:componentName,amount:9}),{name:componentName,value:9});
  assert.deepEqual(await client.query(ref('componentRead'),{name:componentName}),{name:componentName,value:9});
  assert.equal(await client.query(ref('read'),{name:componentName}),null);
  log(`${mode}:owning child component calls and namespace isolation`);
  await cronProgress(client,mode);
}
async function cronProgress(client,mode){
  const initial=(await client.query(ref('read'),{name:'cron-counter'}))?.value??0;
  let progressed=false;
  for(let i=0;i<150;i++){
    const current=await client.query(ref('read'),{name:'cron-counter'});
    if(current?.value>initial){progressed=true;break;}await delay(100);
  }
  assert.ok(progressed,'owning cron did not dispatch within15seconds');
  log(`${mode}:owning cron activation and durable-alias dispatch`);
}
const aliasRef=name=>makeFunctionReference(`AliasCounter:${name}`);
async function aliases(client,amount,expected,expectedHandle){
  assert.deepEqual(await client.mutation(aliasRef('increment'),{name:'alias-shared',amount}),{name:'alias-shared',value:expected});
  assert.deepEqual(await client.query(aliasRef('read'),{name:'alias-shared'}),{name:'alias-shared',value:expected});
  const metadata=await client.query(aliasRef('metadata'),{});
  assert.deepEqual(metadata,{Name:'AliasCounter:metadata',ComponentPath:'',Type:'query',Visibility:'public'});
  const handle=await client.query(aliasRef('handle'),{});
  if(expectedHandle)assert.equal(handle,expectedHandle);
  assert.deepEqual(await client.query(ref('handleRead'),{handle:expectedHandle??handle,name:'alias-shared'}),
    {name:'alias-shared',value:expected});
  await assert.rejects(client.query(ref('handleRead'),{handle:'function://invalid',name:'alias-shared'}));
  await assert.rejects(client.query(makeFunctionReference('AliasNames:read'),{name:'alias-shared'}));
  return handle;
}
async function metrics(){
  const response=await fetch(url+'/metrics',{signal:AbortSignal.timeout(10000)});
  assert.ok(response.ok,'isolated metrics endpoint failed');
  const text=await response.text();
  const retries=[...text.matchAll(/^\w*occ_retries_total_sum(?:\{[^}]*\})?\s+([\d.e+-]+)$/gmi)];
  await fs.writeFile(path.join(state,'occ-metrics.txt'),retries.map(m=>m[0]).join('\n')+'\n');
  assert.ok(retries.length,'actual owning OCC retry metric was absent');
  return retries.reduce((n,m)=>n+Number(m[1]),0);
}
async function mutationAfterKnownOccRefusal(client,args,counts){
  for(let attempt=0;attempt<4;attempt++){
    try{return await client.mutation(ref('increment'),args,{skipQueue:true});}
    catch(error){
      let refusal;try{refusal=JSON.parse(error.message);}catch{}
      // Only this owning refusal proves the attempted transaction did not
      // commit. A timeout, disconnect or any other error must never be retried.
      if(refusal?.code!=='OptimisticConcurrencyControlFailure'||attempt===3)throw error;
      counts.knownOccRefusals++;
      await delay(50*(attempt+1));
    }
  }
  throw new Error('unreachable bounded OCC retry');
}
async function waitFor(values,predicate,label){
  for(let i=0;i<150;i++){if(values.some(predicate))return;await delay(100);}
  throw new Error(`subscription did not observe ${label}`);
}

try{
  summary.nodeExecutable=proofNode;summary.nodeVersion=(await run(proofNode,['--version'])).trim();
  await run(process.execPath,[path.join(root,'npm-packages/system-udfs/node_modules/typescript/bin/tsc'),
    '--noEmit','--allowJs','--checkJs','--strict','--skipLibCheck','--module','esnext','--moduleResolution','bundler',
    '--target','es2022',path.join(component,'counter.js')],project);
  log('private existing-component fixture passes strict owning SDK typecheck');
  key=(await run(binary,['keygen','admin-key','--instance-name',deployment,'--instance-secret',secret])).trim();
  await start('v8');
  await run(process.execPath,[path.join(root,'npm-packages/convex/bin/main.js'),'deploy','--url',url,'--admin-key',key,
    '--typecheck','disable','--codegen','disable','--skip-workos-check'],project);
  await changeEnvironment([{name:'PROOF_ENV',value:'deployment-value'}]);
  const client=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  assert.deepEqual(await client.mutation(ref('increment'),{name:'shared',amount:1}),{name:'shared',value:1});
  const v8Handle=await common(client,'V8');
  await capabilities(client,'V8');
  const aliasHandle=await aliases(client,1,1);
  const aliasBefore=(await docs()).find(doc=>doc.name==='alias-shared');
  const graphPrefix=path.join(state,'sdk-prepared-graph');
  await run(process.execPath,[path.join(root,'npm-packages/convex/bin/main.js'),'deploy','--url',url,'--admin-key',key,
    '--typecheck','disable','--codegen','disable','--skip-workos-check','--write-push-request',graphPrefix],project);
  const preparedGraph=JSON.parse(await fs.readFile(graphPrefix+'.json','utf8'));
  delete preparedGraph.adminKey;
  await fs.writeFile(graphPrefix+'.json',JSON.stringify(preparedGraph),{mode:0o600});
  assert.ok(preparedGraph.appDefinition.definition,'SDK app declaration missing');
  assert.equal(preparedGraph.componentDefinitions.length,1,'SDK child component declaration missing');
  const before=(await docs()).find(doc=>doc.name==='shared');assert.ok(before._id);
  const deployed=await post('/api/get_config_hashes',{adminKey:key});
  const module=deployed.moduleHashes.find(m=>m.path==='Counter.js');assert.ok(module,'Counter.js metadata missing');
  const httpModule=deployed.moduleHashes.find(m=>m.path==='http.js');assert.ok(httpModule,'http.js metadata missing');
  const hashCached=hash(await fs.readFile(assembly));
  const catalog=JSON.parse(await run(dotnet,[host,'--describe',assembly,hashCached]));
  const admin=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true});admin.setAdminAuth(key);
  const specs=await admin.query(makeFunctionReference('_system/cli/modules:apiSpec'),{});
  // HTTP api-spec entries contain a route instead of an ordinary identifier.
  // Their explicit route bindings are built and verified separately below.
  const identifiers=specs.filter(s=>typeof s.identifier==='string').map(s=>s.identifier.replace('.js:',':'));
  const aliasCapsule=JSON.parse(await fs.readFile(path.join(nativeCapsules,'AliasCounter.js'),'utf8'));
  const nativeFunctions=catalog.functions.filter(fn=>fn.kind!=='httpAction').flatMap(fn=>{
    const alias=aliasCapsule.functionAliases.find(alias=>alias.entryPoint===fn.functionPath);
    const functionPath=alias?.functionPath??fn.functionPath;
    return identifiers.includes(functionPath)?[{...fn,functionPath,entryPoint:alias?.entryPoint}]:[];
  });
  const httpBindings=[{functionPath:'Counter:httpEcho',method:'POST',path:'/native/echo'},
    {functionPath:'Counter:httpEcho',method:'GET',path:'/native/prefix/*'},
    {functionPath:'Counter:httpMutation',method:'POST',path:'/native/mutation'},
    {functionPath:'Counter:httpHang',method:'POST',path:'/native/hang'}];
  for(const binding of httpBindings)assert.ok(catalog.functions.some(fn=>fn.functionPath===binding.functionPath&&fn.kind==='httpAction'));
  const hostArtifacts=(await fs.readdir(path.dirname(host),{withFileTypes:true})).filter(f=>f.isFile());
  const artifacts=await Promise.all(hostArtifacts.map(async file=>{const p=path.join(path.dirname(host),file.name);return {path:p,sha256:hash(await fs.readFile(p))};}));
  const manifest=path.join(state,'native-manifest.json');
  const nativeManifest={version:1,maxWorkers:8,worker:{program:dotnet,programSha256:hash(await fs.readFile(dotnet)),
    arguments:[host],artifacts,framework:await pinnedFramework(dotnet),profile:'restricted-first-party',memoryMiB:256,invocationTimeoutMs:16000,maxInvocations:500},
    functions:[...nativeFunctions.map(fn=>{const own=deployed.moduleHashes.find(m=>m.path===fn.functionPath.split(':')[0]+'.js');assert.ok(own,'owning function module metadata missing');return {deployment,componentPath:'',functionPath:fn.functionPath,entryPoint:fn.entryPoint,kind:fn.kind,
      assemblyPath:assembly,assemblySha256:hashCached,assemblyDependencies:[],moduleSha256:Buffer.from(own.hash,'hex').toString('base64')};}),
      ...httpBindings.map(binding=>({deployment,componentPath:'',functionPath:binding.functionPath,kind:'httpAction',
        assemblyPath:assembly,assemblySha256:hashCached,assemblyDependencies:[],moduleSha256:Buffer.from(module.hash,'hex').toString('base64'),
        httpRoute:{modulePath:'http.js',moduleSha256:Buffer.from(httpModule.hash,'hex').toString('base64'),method:binding.method,path:binding.path}}))]};
  Object.assign(summary,{assemblySha256:hashCached,hostSha256:hash(await fs.readFile(host)),
    backendSha256:hash(await fs.readFile(binary)),deployedModuleSha256:module.hash});
  await fs.writeFile(manifest,JSON.stringify(nativeManifest));
  await stop();
  const refused=structuredClone(nativeManifest);
  refused.functions.find(fn=>fn.functionPath==='Counter:read').moduleSha256='stale-module';
  refused.functions.find(fn=>fn.functionPath==='Counter:increment').assemblySha256='00'.repeat(32);
  refused.functions.find(fn=>fn.httpRoute?.path==='/native/echo').httpRoute.moduleSha256='stale-router';
  refused.functions.find(fn=>fn.functionPath==='Counter:httpMutation').moduleSha256='stale-handler';
  const refusalManifest=path.join(state,'refusal-manifest.json');
  await fs.writeFile(refusalManifest,JSON.stringify(refused));
  await start('native-refusal',refusalManifest);
  const refusalClient=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  await assert.rejects(refusalClient.query(ref('read'),{name:'shared'}));
  await assert.rejects(refusalClient.mutation(ref('increment'),{name:'shared',amount:1000}));
  assert.ok(!(await fetch(siteUrl+'/native/echo',{method:'POST',body:'refused',signal:AbortSignal.timeout(10000)})).ok);
  assert.ok(!(await fetch(siteUrl+'/native/mutation',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({name:'http-refusal',amount:1000}),signal:AbortSignal.timeout(10000)})).ok);
  assert.equal((await docs()).find(doc=>doc.name==='shared').value,1);
  assert.ok(!(await docs()).some(doc=>doc.name==='http-refusal'));
  log('stale module/router/handler and assembly digests refuse without fallback or write');
  await stop();
  await start('native',manifest);
  const native=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  assert.deepEqual(await native.query(ref('read'),{name:'shared'}),{name:'shared',value:1});
  const nativeHandle=await common(native,'native');assert.equal(nativeHandle,v8Handle);log('function handle stable across runtime switch');
  await capabilities(native,'native');
  await aliases(native,2,3,aliasHandle);
  assert.equal((await docs()).find(doc=>doc.name==='alias-shared')._id,aliasBefore._id);
  log('native overlay binds a different CLR module while retaining public address, metadata, handle and documentID');
  const baseRetries=await metrics();
  // ConvexHttpClient deliberately queues ordinary mutations. Explicitly bypass
  // that client queue to overlap actual transactions and exercise owner retries.
  const occCounts={knownOccRefusals:0};
  const result=await Promise.all(Array.from({length:12},()=>mutationAfterKnownOccRefusal(native,{name:'shared',amount:1},occCounts)));
  assert.equal((await native.query(ref('read'),{name:'shared'})).value,13);
  assert.equal(new Set(result.map(r=>r.value)).size,12);
  const retryDelta=(await metrics())-baseRetries;assert.ok(retryDelta>0,'no actual OCC retry was recorded');
  log('actual OCC retries and no lost updates',{retryDelta,...occCounts});
  assert.equal((await docs()).find(doc=>doc.name==='shared')._id,before._id);log('stored document ID preserved across switch');
  assert.deepEqual(await native.query(ref('filtered'),{name:'shared',amount:13}),[{name:'shared',value:13}]);
  await assert.rejects(native.query(ref('page'),{name:'shared',cursor:null}));
  const page=await native.query(ref('page'),{name:'shared'});assert.ok(page);log('index filter, pagination and invalid cursor refusal');
  const insertion=await admin.mutation(makeFunctionReference('_system/frontend/addDocument:default'),
    {table:'runtimeCounters',documents:Array.from({length:5},(_,i)=>({name:'paged-rows',value:i+1}))});
  assert.equal(insertion.success,true);
  const pages=[];
  let cursor;
  for(let i=0;i<5;i++){
    const next=await native.query(ref('page'),{name:'paged-rows',...(cursor?{cursor}:{})});
    pages.push(next);
    assert.ok(next.Page.length<=2);
    if(next.IsDone)break;
    assert.equal(typeof next.ContinueCursor,'string');
    cursor=next.ContinueCursor;
  }
  assert.equal(pages.at(-1).IsDone,true);
  assert.deepEqual(pages.flatMap(page=>page.Page.map(doc=>doc.value)).sort((a,b)=>a-b),[1,2,3,4,5]);
  await assert.rejects(native.query(ref('page'),{name:'paged-rows',cursor:'invalid-cursor'}));
  log('real pagination continuation has no duplicate or missing rows');
  reactive=new ConvexClient(url,{skipConvexDeploymentUrlCheck:true,webSocketConstructor:WebSocket,logger:false});
  const values=[];
  const unsubscribe=reactive.onUpdate(ref('read'),{name:'range-entrant'},value=>values.push(value));
  await waitFor(values,value=>value===null,'empty range');
  await native.mutation(ref('increment'),{name:'range-entrant',amount:5});
  await waitFor(values,value=>value?.value===5,'range entrant');
  await native.mutation(ref('increment'),{name:'range-entrant',amount:2});
  await waitFor(values,value=>value?.value===7,'updated indexed row');
  unsubscribe();log('real WebSocket range entrant and point update invalidation');
  const envValues=[];
  const unsubscribeEnv=reactive.onUpdate(ref('environmentValue'),{name:'PROOF_ENV'},value=>envValues.push(value));
  await waitFor(envValues,value=>value==='deployment-value','deployed environment value');
  await changeEnvironment([{name:'PROOF_ENV',value:'changed-value'}]);
  await waitFor(envValues,value=>value==='changed-value','updated owning environment');
  await changeEnvironment([{name:'PROOF_ENV',value:null}]);
  await waitFor(envValues,value=>value===null,'unset owning environment');
  unsubscribeEnv();log('real WebSocket environment update and unset invalidation');
  await assert.rejects(native.query(ref('hang'),{}));
  await assert.rejects(native.mutation(ref('hangMutation'),{name:'deadline-write'}));
  assert.equal(await native.query(ref('read'),{name:'deadline-write'}),null);
  await assert.rejects(native.action(ref('hangAction'),{}));
  // The owning action worker has a 16-second wall deadline. The observer must
  // wait past it to verify the backend failure rather than cancel the request.
  const failedHttp=await fetch(siteUrl+'/native/hang',{method:'POST',signal:AbortSignal.timeout(25000)});
  assert.ok(!failedHttp.ok);
  assert.equal((await native.mutation(ref('increment'),{name:'after-kill',amount:1})).value,1);log('noncooperative deadline kill and database recovery');
  const failureLogs=await fetch(url+'/api/stream_function_logs?cursor=0',{headers:{Authorization:`Convex ${key}`},
    signal:AbortSignal.timeout(10000)});
  assert.ok(failureLogs.ok);const nativeFailures=(await failureLogs.json()).entries;
  for(const identifier of ['Counter:hang','Counter:hangMutation','Counter:hangAction']){
    assert.ok(nativeFailures.some(entry=>entry.kind==='Completion'&&entry.identifier===identifier&&
      entry.environment==='dotNet'&&entry.error),`owning failure completion lacks native classification for ${identifier}`);
  }
  assert.ok(nativeFailures.some(entry=>entry.kind==='Completion'&&entry.identifier==='POST /native/hang'&&
    entry.environment==='dotNet'&&entry.error),'owning failed HTTP completion lacks native classification');
  log('native deadline query/mutation/action/HTTP failures retain owning runtime classification and roll back');
  await reactive.close();reactive=undefined;
  for(const direction of ['native-parent','V8-parent']){
    const mixed=structuredClone(nativeManifest);
    mixed.functions=mixed.functions.filter(fn=>direction==='native-parent'
      ?!['Counter:read','Counter:increment','Counter:fail'].includes(fn.functionPath)
      :['Counter:read','Counter:increment','Counter:fail'].includes(fn.functionPath));
    const mixedManifest=path.join(state,`${direction}-manifest.json`);
    await fs.writeFile(mixedManifest,JSON.stringify(mixed));
    await stop();await start(direction,mixedManifest);
    const mixedClient=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
    assert.deepEqual(await mixedClient.query(ref('nestedRead'),{name:'shared'}),{name:'shared',value:13});
    assert.deepEqual(await mixedClient.mutation(ref('nestedIncrement'),{name:`mixed-${direction}`,amount:2}),
      {name:`mixed-${direction}`,value:2});
    assert.deepEqual(await mixedClient.action(ref('incrementAction'),{name:`action-mixed-${direction}`,amount:3}),
      {name:`action-mixed-${direction}`,value:3});
    const rollbackName=`mixed-rollback-${direction}`;
    assert.deepEqual(await mixedClient.mutation(ref('nestedRollback'),{name:rollbackName}),
      {operation:rollbackName,reason:'refused'});
    assert.deepEqual(await mixedClient.query(ref('read'),{name:rollbackName}),{name:rollbackName,value:1});
    log(`mixed ${direction} nested query/mutation and action callback`);
  }
  const nativeChild={...nativeManifest,functions:nativeManifest.functions.filter(fn=>fn.functionPath==='Counter:hang')};
  const nativeChildManifest=path.join(state,'native-child-failure.json');
  await fs.writeFile(nativeChildManifest,JSON.stringify(nativeChild));
  await stop();await start('native-child-failure',nativeChildManifest);
  const v8Parent=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  await assert.rejects(v8Parent.query(ref('nestedHang'),{}));
  const parentLogs=await fetch(url+'/api/stream_function_logs?cursor=0',{headers:{Authorization:`Convex ${key}`},
    signal:AbortSignal.timeout(10000)});
  assert.ok(parentLogs.ok);
  assert.ok((await parentLogs.json()).entries.some(entry=>entry.kind==='Completion'&&entry.identifier==='Counter:nestedHang'&&
    entry.environment==='isolate'&&entry.error),'native child failure relabeled the V8 parent');
  log('native child failure preserves V8 parent completion provenance');
  const saturated={...nativeManifest,maxWorkers:1};
  const saturatedManifest=path.join(state,'saturated-manifest.json');
  await fs.writeFile(saturatedManifest,JSON.stringify(saturated));
  await stop();await start('bounded-pool',saturatedManifest);
  const bounded=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  const refusalStarted=Date.now();
  await assert.rejects(bounded.query(ref('nestedRead'),{name:'shared'}));
  assert.ok(Date.now()-refusalStarted<5000,'saturated nested pool did not refuse promptly');
  assert.equal((await bounded.query(ref('read'),{name:'shared'})).value,13);
  const actionRefusalStarted=Date.now();
  await assert.rejects(bounded.action(ref('incrementAction'),{name:'saturated-action',amount:1}));
  assert.ok(Date.now()-actionRefusalStarted<5000,'saturated action callback did not refuse promptly');
  const httpRefusalStarted=Date.now();
  assert.ok(!(await fetch(siteUrl+'/native/mutation',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({name:'saturated-http',amount:1}),signal:AbortSignal.timeout(5000)})).ok);
  assert.ok(Date.now()-httpRefusalStarted<5000,'saturated HTTP callback did not refuse promptly');
  assert.equal(await bounded.query(ref('read'),{name:'saturated-action'}),null);
  assert.equal(await bounded.query(ref('read'),{name:'saturated-http'}),null);
  log('saturated nested worker pool refuses without deadlock and recovers');
  if(nativeCapsules){
    assert.ok(path.isAbsolute(nativeCapsules),'native capsule directory must be absolute');
    const runtimeOnly={...nativeManifest,functions:[]};
    const runtimeManifest=path.join(state,'native-runtime-only.json');
    await fs.writeFile(runtimeManifest,JSON.stringify(runtimeOnly));
    await stop();await start('native-admission',runtimeManifest);
    const capsuleFiles=await fs.readdir(nativeCapsules);
    const nativeModules=await Promise.all(capsuleFiles.filter(file=>file.endsWith('.js')).map(async file=>({path:file,
      source:await fs.readFile(path.join(nativeCapsules,file),'utf8'),sourceMap:null,environment:'dotNet'})));
    const nativeSchema=nativeModules.find(module=>module.path==='schema.js');assert.ok(nativeSchema);
    const nativeGraph=structuredClone(preparedGraph);
    nativeGraph.appDefinition.schema=nativeSchema;
    nativeGraph.appDefinition.changedModules=nativeModules.filter(module=>module.path!=='schema.js');
    nativeGraph.appDefinition.unchangedModuleHashes=[];
    nativeGraph.dryRun=false;nativeGraph.forCodegen=false;
    const pushRequest={...nativeGraph,adminKey:key};
    const graphPath=path.join(state,'native-prepared-graph.json');
    await fs.writeFile(graphPath,JSON.stringify(nativeGraph),{mode:0o600});
    const beforeNative=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
    assert.equal(beforeNative.find(module=>module.path==='NodeActions.js')?.environment,'node');
    const nodeRef=makeFunctionReference('NodeActions:increment');
    const nodeClient=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
    assert.deepEqual(await nodeClient.action(nodeRef,{name:'node-switch',amount:2}),{name:'node-switch',value:2});
    await changeEnvironment([{name:'DBM_OIDC_ISSUER',value:'https://auth.example.org'},
      {name:'DBM_OIDC_AUDIENCE',value:'native-proof'}]);
    const unauthorized={...pushRequest,adminKey:'not-an-authorized-deployment-key'};
    assert.ok(!(await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify(unauthorized),signal:AbortSignal.timeout(10000)})).ok);
    const wrong=structuredClone(pushRequest);
    const invalidCapsule=JSON.parse(wrong.appDefinition.changedModules.find(module=>module.path==='Counter.js').source);
    invalidCapsule.assembly.name='../host.dll';
    wrong.appDefinition.changedModules.find(module=>module.path==='Counter.js').source=JSON.stringify(invalidCapsule);
    assert.ok(!(await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify(wrong),signal:AbortSignal.timeout(10000)})).ok);
    const mismatched=structuredClone(pushRequest);
    const invalidRouter=JSON.parse(mismatched.appDefinition.changedModules.find(module=>module.path==='http.js').source);
    invalidRouter.definition.routes[0].functionPath='Counter:httpMutation';
    mismatched.appDefinition.changedModules.find(module=>module.path==='http.js').source=JSON.stringify(invalidRouter);
    assert.ok(!(await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify(mismatched),signal:AbortSignal.timeout(15000)})).ok);
    const missingAlias=structuredClone(pushRequest);
    const badAlias=JSON.parse(missingAlias.appDefinition.changedModules.find(module=>module.path==='AliasCounter.js').source);
    badAlias.functionAliases[0].entryPoint='AliasNames:missing';
    missingAlias.appDefinition.changedModules.find(module=>module.path==='AliasCounter.js').source=JSON.stringify(badAlias);
    assert.ok(!(await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify(missingAlias),signal:AbortSignal.timeout(15000)})).ok);
    for(const selected of ['app','component']){
      const unsupported=structuredClone(pushRequest);
      const definition=selected==='app'?unsupported.appDefinition.definition:unsupported.componentDefinitions[0].definition;
      definition.environment='dotNet';
      const refusal=await fetch(url+'/api/deploy2/start_push',{method:'POST',headers:{'Content-Type':'application/json'},
        body:JSON.stringify(unsupported),signal:AbortSignal.timeout(20000)});
      assert.equal(refusal.status,400);
      const body=await refusal.json();
      // ModuleJson admission owns the first refusal: changing a JS module's
      // environment does not manufacture a hash-bound native capsule.
      assert.equal(body.code,'InvalidConfig');assert.match(body.message,/Invalid native module capsule/);
    }
    assert.equal((await nodeClient.query(ref('read'),{name:'shared'})).value,13);
    log('native admission refuses unauthorized key, filesystem path, missing CLR alias and mismatched router without activation');
    const activated=await nativeDeploy(url,graphPath);
    assert.equal(activated.code,0,activated.output);
    assert.equal(JSON.parse(activated.output.trim()).outcome,'confirmed');
    log('native .NET deployment CLI preserves the SDK-prepared app/component graph through owning deploy2');
    const admitted=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
    for(const file of ['Counter.js','NodeActions.js','http.js','auth.config.js','crons.js'])assert.equal(admitted.find(module=>module.path===file)?.environment,'dotNet');
    await changeEnvironment([{name:'PROOF_ENV',value:'deployment-value'}]);
    const admittedClient=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
    assert.equal(await common(admittedClient,'capsule'),v8Handle);
    await capabilities(admittedClient,'capsule');
    await aliases(admittedClient,3,6,aliasHandle);
    assert.equal((await docs()).find(doc=>doc.name==='alias-shared')._id,aliasBefore._id);
    log('persisted native capsule maps durable public aliases to a different CLR module');
    const logResponse=await fetch(url+'/api/stream_function_logs?cursor=0',{headers:{Authorization:`Convex ${key}`},
      signal:AbortSignal.timeout(10000)});
    assert.ok(logResponse.ok);const executionLogs=(await logResponse.json()).entries;
    for(const identifier of ['Counter:insertSearch','Counter:vectors','AliasCounter:metadata']){
      assert.ok(executionLogs.some(entry=>entry.kind==='Completion'&&entry.identifier===identifier&&
        entry.environment==='dotNet'&&!entry.error),`owning completion log lacks native classification for ${identifier}`);
    }
    log('native query/mutation/action completions retain runtime classification and durable public names');
    assert.ok(executionLogs.some(entry=>entry.kind==='Completion'&&entry.identifier==='POST /native/echo'&&
      entry.environment==='dotNet'&&!entry.error),'owning native HTTP completion lacks runtime classification');
    log('native HTTP completion retains owning runtime classification');
    // get_config_hashes intentionally omits legacy authInfo when auth.config
    // exists. Read the authoritative private system table through its owner.
    const authInfo=async()=>(await admin.query(makeFunctionReference('_system/frontend/listAuthProviders:default'),{}))
      .map(({applicationID,domain})=>({applicationID,domain}));
    assert.deepEqual(await authInfo(),[{applicationID:'native-proof',domain:'https://auth.example.org'}]);
    await changeEnvironment([{name:'DBM_OIDC_AUDIENCE',value:'native-proof-updated'}]);
    assert.deepEqual(await authInfo(),[{applicationID:'native-proof-updated',domain:'https://auth.example.org'}]);
    for(const [changes,code] of [
      [[{name:'DBM_OIDC_ISSUER',value:'not a URL'}],'InvalidProviderDomainUrl'],
      [[{name:'DBM_OIDC_AUDIENCE',value:null}],'AuthConfigMissingEnvironmentVariable']]){
      const rejected=await fetch(url+'/api/update_environment_variables',{method:'POST',
        headers:{'Content-Type':'application/json',Authorization:`Convex ${key}`},
        body:JSON.stringify({changes}),signal:AbortSignal.timeout(10000)});
      assert.equal(rejected.status,400,'owning native auth validation must reject invalid/missing variable as a bad request');
      assert.equal((await rejected.json()).code,code);
      assert.deepEqual(await authInfo(),[{applicationID:'native-proof-updated',domain:'https://auth.example.org'}]);
    }
    assert.equal(await admittedClient.query(ref('environmentValue'),{name:'DBM_OIDC_ISSUER'}),'https://auth.example.org');
    assert.equal(await admittedClient.query(ref('environmentValue'),{name:'DBM_OIDC_AUDIENCE'}),'native-proof-updated');
    log('owning native auth reevaluates environment changes and rolls invalid/missing updates back');
    assert.deepEqual(await admittedClient.action(nodeRef,{name:'node-switch',amount:3}),{name:'node-switch',value:5});
    assert.equal((await docs()).find(doc=>doc.name==='shared')._id,before._id);
    log('authenticated native-only capsule schema/auth/HTTP/functions activation and Node action switch');
    const beforeNativeRetries=await metrics();
    const capsuleOccCounts={knownOccRefusals:0};
    const nativeWrites=await Promise.all(Array.from({length:8},()=>mutationAfterKnownOccRefusal(admittedClient,{name:'capsule-occ',amount:1},capsuleOccCounts)));
    assert.equal(new Set(nativeWrites.map(value=>value.value)).size,8);
    assert.equal((await admittedClient.query(ref('read'),{name:'capsule-occ'})).value,8);
    const nativeRetries=(await metrics())-beforeNativeRetries;assert.ok(nativeRetries>0);
    log('persisted native capsules retain owning OCC',{retryDelta:nativeRetries,...capsuleOccCounts});
    await stop();await start('capsule-restart',runtimeManifest);
    const restarted=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
    assert.deepEqual(await restarted.action(nodeRef,{name:'node-switch',amount:1}),{name:'node-switch',value:6});
    assert.equal((await restarted.query(ref('read'),{name:'shared'})).value,13);
    await aliases(restarted,4,10,aliasHandle);
    assert.deepEqual(await restarted.query(ref('componentRead'),{name:'component-capsule'}),{name:'component-capsule',value:9});
    await http(restarted,'capsule-restart');
    await cronProgress(restarted,'capsule-restart');
    log('persisted native capsule restart reconstructs admitted artifacts from owning storage');
    await lostFinishReply(graphPath);
    // Re-deploy the real same-source JavaScript package through the original
    // owner, returning the Node module to its original owning runtime.
    await run(process.execPath,[path.join(root,'npm-packages/convex/bin/main.js'),'deploy','--url',url,'--admin-key',key,
      '--typecheck','disable','--codegen','disable','--skip-workos-check'],project);
    assert.equal((await post('/api/get_config_hashes',{adminKey:key})).moduleHashes.find(module=>module.path==='NodeActions.js')?.environment,'node');
    assert.deepEqual(await restarted.action(nodeRef,{name:'node-switch',amount:1}),{name:'node-switch',value:7});
    await aliases(restarted,5,15,aliasHandle);
    await cronProgress(restarted,'V8-redeploy');
    assert.equal((await docs()).find(doc=>doc.name==='alias-shared')._id,aliasBefore._id);
    assert.deepEqual(await restarted.query(ref('componentRead'),{name:'component-capsule'}),{name:'component-capsule',value:9});
    log('owning deploy2 redeploy rolls native capsules back to V8/Node with stored identity intact');
    summary.nativeCapsules=nativeModules.map(module=>({path:module.path,sha256:hash(Buffer.from(module.source))}));
  }
  // Restore upstream execution on the same data to establish a reviewable
  // rollback boundary. Only the private synthetic backend is restarted.
  await stop();await start('rollback-v8');
  const restored=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  assert.equal((await restored.query(ref('read'),{name:'shared'})).value,13);
  assert.deepEqual(canonical(await restored.query(ref('page'),{name:'shared'})),canonical(page));
  assert.deepEqual(canonical(await restored.query(ref('page'),{name:'paged-rows'})),canonical(pages[0]));
  assert.equal((await docs()).find(doc=>doc.name==='shared')._id,before._id);log('V8 rollback retains native-written data and IDs');
  await fs.writeFile(path.join(state,'summary.json'),JSON.stringify(summary,null,2));
  console.log(`Actual backend parity proof passed. Evidence: ${path.join(state,'summary.json')}`);
}finally{
  if(reactive)await reactive.close();
  await stop();
  await fs.writeFile(path.join(state,'summary.json'),JSON.stringify(summary,null,2));
}
