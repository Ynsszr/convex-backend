// Actual DBM cloud sources on the patched backend, with fresh private state.
// The installed runtime and the original proof-backend pin are never changed.
import assert from 'node:assert/strict';
import {pinnedFramework} from './pinned_framework.mjs';
import {spawn} from 'node:child_process';
import {createHash,randomBytes,randomUUID,generateKeyPairSync,sign} from 'node:crypto';
import fs from 'node:fs/promises';
import {createWriteStream} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';

const root=fileURLToPath(new URL('../../..',import.meta.url));
const [dotnet,host,dbm,nativeCapsules,nativeCli,nativeSchemaCapsule,referenceConvexDirectory]=process.argv.slice(2);
assert.ok([dotnet,host,dbm,nativeCapsules,nativeCli].every(p=>p&&path.isAbsolute(p)),
  'usage: dbm_backend_parity.mjs /absolute/dotnet /absolute/Host.dll /absolute/dbm /absolute/native-platform-capsules /absolute/Cli.dll');
assert.ok(!nativeSchemaCapsule||path.isAbsolute(nativeSchemaCapsule),'native schema capsule file must be absolute');
assert.ok(!referenceConvexDirectory||path.isAbsolute(referenceConvexDirectory),'frozen reference source directory must be absolute');
const binary=path.join(root,'target/debug/convex-local-backend');
const {ConvexHttpClient,ConvexClient}=await import(path.join(root,'npm-packages/convex/dist/esm/browser/index.js'));
const {makeFunctionReference}=await import(path.join(root,'npm-packages/convex/dist/esm/server/index.js'));
const state=await fs.mkdtemp('/tmp/convex-dotnet-dbm-parity-');await fs.chmod(state,0o700);
const workspace=path.join(state,'dbm'),project=path.join(workspace,'apps/cloud');
await fs.mkdir(project,{recursive:true});
await fs.cp(referenceConvexDirectory||path.join(dbm,'apps/cloud/convex'),path.join(project,'convex'),{recursive:true});
await fs.copyFile(path.join(dbm,'apps/cloud/package.json'),path.join(project,'package.json'));
await fs.copyFile(path.join(dbm,'tsconfig.json'),path.join(workspace,'tsconfig.json'));
await fs.symlink(path.join(dbm,'node_modules'),path.join(workspace,'node_modules'));
const proofNode=process.env.CONVEX_DOTNET_PROOF_NODE||process.execPath;
const env={PATH:path.dirname(proofNode)+path.delimiter+process.env.PATH,HOME:state,LANG:'C.UTF-8',CI:'1',
  SENTRY_DSN:'',DISABLE_BEACON:'true',RUST_LOG:'warn',DISABLE_METRICS_ENDPOINT:'false'};
const deployment='dbm-native-proof',secret=randomBytes(32).toString('hex'),bootstrap=randomBytes(32).toString('hex');
let key='',backend,reactive;
const summary={state,source:referenceConvexDirectory||'actual DBM apps/cloud/convex',base:'df8338dd8974674cf0c4cfb42aff4b50f790bc9c',checks:[]};
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const ref=makeFunctionReference;
const log=(name,detail)=>{summary.checks.push({name,detail});console.log(`PASS ${name}`);};
async function run(program,args,cwd=state,extra={}){
  return await new Promise((resolve,reject)=>{
    const child=spawn(program,args,{cwd,env:{...env,...extra},stdio:['ignore','pipe','pipe']});
    let output='';
    for(const stream of [child.stdout,child.stderr])stream.on('data',chunk=>output=(output+chunk.toString()).slice(-1024*1024));
    child.on('error',reject);child.on('exit',code=>{
      output=output.replaceAll(key||'never-secret','[isolated-key]').replaceAll(bootstrap,'[isolated-bootstrap]');
      code===0?resolve(output):reject(new Error(`private command failed (${code}): ${output}`));
    });
  });
}
async function freePort(){return await new Promise((resolve,reject)=>{
  const listener=net.createServer();listener.on('error',reject);listener.listen(0,'127.0.0.1',()=>{
    const port=listener.address().port;listener.close(()=>resolve(port));
  });
});}
const port=await freePort(),sitePort=await freePort(),url=`http://127.0.0.1:${port}`;
const artifacts=await Promise.all((await fs.readdir(path.dirname(host),{withFileTypes:true})).filter(f=>f.isFile()).map(async file=>{
  const p=path.join(path.dirname(host),file.name);return {path:p,sha256:hash(await fs.readFile(p))};
}));
const manifest=path.join(state,'runtime.json');
await fs.writeFile(manifest,JSON.stringify({version:1,maxWorkers:8,worker:{program:dotnet,
  programSha256:hash(await fs.readFile(dotnet)),arguments:[host],artifacts,framework:await pinnedFramework(dotnet),profile:'restricted-first-party',
  memoryMiB:256,invocationTimeoutMs:16000,maxInvocations:500},functions:[]}),{mode:0o600});
async function start(mode){
  const output=createWriteStream(path.join(state,`backend-${mode}.log`));
  backend=spawn(binary,[path.join(state,'database.sqlite3'),'--interface','127.0.0.1','--port',String(port),
    '--site-proxy-port',String(sitePort),'--instance-name',deployment,'--instance-secret',secret,
    '--local-storage',path.join(state,'storage'),'--disable-beacon'],
    {cwd:state,env:{...env,CONVEX_DOTNET_MANIFEST:manifest},stdio:['ignore','pipe','pipe']});
  backend.stdout.pipe(output);backend.stderr.pipe(output);
  for(let i=0;i<150;i++){
    if(backend.exitCode!==null)throw new Error(`private backend startup failed; ${state}`);
    try{if((await fetch(url+'/version',{signal:AbortSignal.timeout(500)})).ok)return;}catch{}
    await delay(100);
  }
  throw new Error('private DBM backend readiness timeout');
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
      const inherited=(await fs.readFile(`/proc/${pid}/environ`)).toString().split('\0');
      if(inherited.includes(`HOME=${state}`))process.kill(Number(pid),'SIGTERM');
    }catch(error){if(!['ENOENT','ESRCH','EACCES','EPERM'].includes(error.code))throw error;}
  }
}
async function post(route,body){
  const response=await fetch(url+route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),
    signal:AbortSignal.timeout(15000)});
  assert.ok(response.ok,`private owner route ${route} refused`);return await response.json();
}
const issuer='https://dbm-native-proof.invalid',secondaryIssuer='https://dbm-native-secondary.invalid',audience='dbm-native-proof';
const pairs=[generateKeyPairSync('rsa',{modulusLength:2048}),generateKeyPairSync('rsa',{modulusLength:2048})];
const jwks=pair=>'data:text/plain;charset=utf-8;base64,'+Buffer.from(JSON.stringify({keys:[{
  ...pair.publicKey.export({format:'jwk'}),kid:'proof',alg:'RS256',use:'sig'}]})).toString('base64');
const jwt=(subject,overrides={})=>{
  const now=Math.floor(Date.now()/1000),header=Buffer.from(JSON.stringify({alg:'RS256',kid:'proof',typ:'JWT'})).toString('base64url');
  const payload=Buffer.from(JSON.stringify({iss:issuer,aud:audience,sub:subject,iat:now,exp:now+3600,...overrides})).toString('base64url');
  const content=header+'.'+payload;return content+'.'+sign('RSA-SHA256',Buffer.from(content),
    pairs[overrides.iss===secondaryIssuer?1:0].privateKey).toString('base64url');
};
function client(subject,overrides={}){
  const client=new ConvexHttpClient(url,{skipConvexDeploymentUrlCheck:true,logger:false});
  if(subject)client.setAuth(jwt(subject,overrides));return client;
}
const query=(client,name,args={})=>client.query(ref(name),args);
const mutation=(client,name,args={})=>client.mutation(ref(name),args);
async function changed(values,predicate,label){
  for(let i=0;i<150;i++){if(values.some(predicate))return;await delay(100);}throw new Error(`DBM subscription missed ${label}`);
}

async function identity(mode){
  const primary=client(`owner-${mode}`),linked=client(`linked-${mode}`,{iss:secondaryIssuer}),other=client(`other-${mode}`);
  for(const invalid of [client('invalid',{aud:'wrong'}),client('invalid',{iss:'https://wrong.invalid'}),
    client('invalid',{exp:Math.floor(Date.now()/1000)-600})])await assert.rejects(query(invalid,'identity:current'));
  const forged=client();const valid=jwt('forged');forged.setAuth(valid.slice(0,valid.lastIndexOf('.')+1)+'invalid');
  await assert.rejects(query(forged,'identity:current'));await assert.rejects(query(client(),'identity:current'));
  await assert.rejects(query(primary,'identity:current'),/Register/);
  const [principal,repeated]=await Promise.all([
    primary.mutation(ref('identity:register'),{},{skipQueue:true}),
    primary.mutation(ref('identity:register'),{},{skipQueue:true})]);
  assert.equal(principal,repeated);const otherId=await mutation(other,'identity:register');assert.notEqual(principal,otherId);
  assert.deepEqual(await query(primary,'identity:current'),{principal,subject:`owner-${mode}`,issuer});
  log(`${mode}:real signed JWT verification, concurrent stable principal registration and auth refusal`);
  const secret=randomBytes(32).toString('hex'),requestKey=randomUUID();
  const target={issuer:secondaryIssuer,subject:`linked-${mode}`};
  const challenge=await mutation(primary,'identity:beginLink',{secret,requestKey,target});
  assert.deepEqual(await mutation(primary,'identity:beginLink',{secret,requestKey,target}),challenge);
  assert.deepEqual(await query(linked,'identity:linkRequest',{principal,requestKey,secret}),challenge);
  assert.deepEqual(await query(linked,'identity:linkReceipt',{challenge:challenge.challenge,secret}),{state:'pending',principal:null});
  await assert.rejects(mutation(linked,'identity:finishLink',{challenge:challenge.challenge,secret:'wrong'}));
  await assert.rejects(mutation(other,'identity:finishLink',{challenge:challenge.challenge,secret}));
  assert.equal(await mutation(linked,'identity:finishLink',{challenge:challenge.challenge,secret}),principal);
  await assert.rejects(mutation(linked,'identity:finishLink',{challenge:challenge.challenge,secret}));
  assert.deepEqual(await query(linked,'identity:linkReceipt',{challenge:challenge.challenge,secret}),{state:'consumed',principal});
  log(`${mode}:exact login handoff request/receipt, target binding and one-use cross-issuer linking`);
  const bindings=await query(linked,'identity:bindings');assert.equal(bindings.length,2);
  const original=bindings.find(binding=>binding.subject===`owner-${mode}`);
  const retainedSecret=randomBytes(32).toString('hex'),attackerSubject=`attacker-${mode}`;
  const retained=await mutation(primary,'identity:beginLink',{secret:retainedSecret,requestKey:randomUUID(),
    target:{issuer,subject:attackerSubject}});
  reactive=new ConvexClient(url,{skipConvexDeploymentUrlCheck:true,webSocketConstructor:WebSocket,logger:false});
  await new Promise((resolve,reject)=>{const deadline=setTimeout(()=>reject(new Error('DBM signed subscription auth deadline')),10000);
    reactive.setAuth(async()=>jwt(`linked-${mode}`,{iss:secondaryIssuer}),authenticated=>{if(authenticated){clearTimeout(deadline);resolve();}});});
  const values=[];const unsubscribe=reactive.onUpdate(ref('identity:bindings'),{},value=>values.push(value));
  await changed(values,rows=>rows.some(binding=>binding.id===original.id&&binding.enabled),'original enabled binding');
  await mutation(linked,'identity:disableBinding',{binding:original.id});
  await changed(values,rows=>rows.some(binding=>binding.id===original.id&&!binding.enabled),'revoked binding');
  unsubscribe();await reactive.close();reactive=undefined;
  const attacker=client(attackerSubject);
  await assert.rejects(mutation(attacker,'identity:finishLink',{challenge:retained.challenge,secret:retainedSecret}),/authority revoked/);
  assert.deepEqual(await query(attacker,'identity:linkReceipt',{challenge:retained.challenge,secret:retainedSecret}),{state:'revoked',principal:null});
  await assert.rejects(query(primary,'identity:current'),/Register or restore/);
  await assert.rejects(mutation(linked,'identity:disableBinding',{binding:bindings.find(binding=>binding.subject===`linked-${mode}`).id}),/at least one/);
  log(`${mode}:reactive link-authority revocation and durable revoked receipt`);
  return {owner:linked,principal,subject:`linked-${mode}`,other,otherId,challenge,linkSecret:secret};
}

async function configuration(actor,mode,admin){
  const owner=actor.owner;
  const organizationRequest={name:`DBM native ${mode}`,requestKey:randomUUID()};
  const organization=await mutation(owner,'platform:createOrganization',organizationRequest);
  const scope={kind:'organization',organization},platform={kind:'platform'};
  const workspaceRequest={organization,name:`Managed ${mode}`,mode:'managed',privacy:'deviceOnly',remoteAllowed:true,requestKey:randomUUID()};
  const workspace=await mutation(owner,'platform:createWorkspace',workspaceRequest);
  const second=await mutation(owner,'platform:createWorkspace',{organization,name:`Observe ${mode}`,mode:'observeOnly',privacy:'deviceOnly',remoteAllowed:false});
  const homePolicy={name:`Managed ${mode}`,revision:1,mode:'managed',privacy:'deviceOnly',remoteAllowed:true,role:'owner'};
  assert.deepEqual(await query(owner,'homeRequests:workspacePolicy',{workspace}),homePolicy);
  const organizationReceipt=await query(owner,'homeRequests:creationStatus',{requestKey:organizationRequest.requestKey});
  assert.deepEqual(organizationReceipt,{kind:'organization',id:organization,name:organizationRequest.name});
  const workspaceReceipt=await query(owner,'homeRequests:creationStatus',{organization,requestKey:workspaceRequest.requestKey});
  assert.equal(workspaceReceipt.kind,'workspace');assert.equal(workspaceReceipt.id,workspace);assert.equal(workspaceReceipt.name,workspaceRequest.name);
  assert.equal(workspaceReceipt.creationFingerprint,JSON.stringify([workspaceRequest.name,workspaceRequest.mode,workspaceRequest.privacy,workspaceRequest.remoteAllowed]));
  assert.equal(await mutation(owner,'platform:createOrganization',organizationRequest),organization);
  assert.equal(await mutation(owner,'platform:createWorkspace',workspaceRequest),workspace);
  for(const name of ['workspacePolicy','creationStatus'])await assert.rejects(query(client(),`homeRequests:${name}`,name==='workspacePolicy'?{workspace}:{requestKey:organizationRequest.requestKey}));
  await assert.rejects(query(actor.other,'homeRequests:workspacePolicy',{workspace}));
  await assert.rejects(query(actor.other,'homeRequests:creationStatus',{organization,requestKey:workspaceRequest.requestKey}));
  assert.equal(await query(actor.other,'homeRequests:creationStatus',{requestKey:organizationRequest.requestKey}),null);
  await assert.rejects(query(owner,'homeRequests:creationStatus',{requestKey:'INVALID'}));
  await assert.rejects(query(owner,'homeRequests:workspacePolicy',{workspace,foreign:true}));
  log(`${mode}:membership-bound home policy and exact organization/workspace creation receipts preserve replay identity`);
  const machineKey=randomBytes(32).toString('hex');
  const computer=await mutation(owner,'platform:enrollComputer',{workspace,name:`Synthetic ${mode}`,placement:'customer',keyHash:hash(Buffer.from(machineKey))});
  const deploymentId=await mutation(owner,'platform:registerDeployment',{workspace,computer,name:`Synthetic OpenClaw ${mode}`});
  const template={scope,templateId:`reader-${mode}`,name:'Reader default',audience:'Readers',description:'A shared preference without native effect authority.',
    edits:[{path:'/ui/prefs/themeMode',value:{kind:'Text',text:'light'}},{path:'/tools/deny',value:{kind:'StringList',items:['browser','canvas']}}],
    expectedRevision:0,requestKey:randomUUID()};
  await assert.rejects(query(client(),'sharedConfiguration:selected',{scope,templateId:template.templateId}));
  assert.equal((await query(owner,'sharedConfiguration:availability',{scope})).writeAllowed,true);
  assert.equal((await query(owner,'sharedConfiguration:availability',{scope:platform})).writeAllowed,false);
  assert.equal(await mutation(owner,'sharedConfiguration:save',template),1);assert.equal(await mutation(owner,'sharedConfiguration:save',template),1);
  const receipt=await query(owner,'sharedConfiguration:requestStatus',{scope,requestKey:template.requestKey});
  assert.equal(receipt.revision,1);assert.match(receipt.fingerprint,/^[a-f0-9]{64}$/);
  await assert.rejects(mutation(owner,'sharedConfiguration:save',{...template,name:'Changed request'}));
  await assert.rejects(mutation(owner,'sharedConfiguration:save',{...template,requestKey:randomUUID()}));
  await assert.rejects(mutation(owner,'sharedConfiguration:save',{...template,requestKey:randomUUID(),edits:[{path:'/tools/apiKey',value:{kind:'Text',text:'forbidden'}}]}));
  await assert.rejects(mutation(owner,'sharedConfiguration:save',{...template,requestKey:randomUUID(),edits:[{path:'/ui/prefs/themeMode',value:{kind:'Text',text:'light',foreign:'field'}}]}));
  const first=await query(owner,'sharedConfiguration:version',{scope,templateId:template.templateId,contentRevision:1});
  assert.deepEqual(first.content.edits,template.edits);
  const next={...template,edits:[{path:'/ui/prefs/themeMode',value:{kind:'Text',text:'dark'}}],expectedRevision:1,requestKey:randomUUID()};
  assert.equal(await mutation(owner,'sharedConfiguration:save',next),2);
  assert.deepEqual((await query(owner,'sharedConfiguration:version',{scope,templateId:template.templateId,contentRevision:1})).content,first.content);
  assert.equal(await mutation(owner,'sharedConfiguration:archive',{scope,templateId:template.templateId,archived:true,expectedRevision:2,requestKey:randomUUID()}),3);
  assert.equal(await mutation(owner,'sharedConfiguration:save',template),1);
  assert.equal((await query(owner,'sharedConfiguration:selected',{scope,templateId:template.templateId})).head.revision,3);
  log(`${mode}:shared exact request fingerprints, immutable versions/archive and unsafe edit refusal`);
  await assert.rejects(query(actor.other,'sharedConfiguration:selected',{scope,templateId:template.templateId}));
  await mutation(owner,'platform:setMembership',{organization,principal:actor.otherId,role:'viewer',expectedMembership:null,expectedRevision:0});
  assert.deepEqual(await query(actor.other,'homeRequests:workspacePolicy',{workspace}),{...homePolicy,role:'viewer'});
  assert.equal((await query(actor.other,'sharedConfiguration:availability',{scope})).writeAllowed,false);
  assert.equal(await query(actor.other,'sharedConfiguration:requestStatus',{scope,requestKey:template.requestKey}),null);
  assert.equal((await query(actor.other,'sharedConfiguration:selected',{scope,templateId:template.templateId})).head.revision,3);
  await assert.rejects(mutation(actor.other,'sharedConfiguration:save',{...next,expectedRevision:3,requestKey:randomUUID()}));
  const publisher={principal:actor.principal,enabled:true,expectedRevision:0,requestKey:randomUUID(),reason:'Private native DBM proof publisher'};
  await assert.rejects(mutation(owner,'sharedConfiguration:configureOperator',publisher));
  assert.equal(await mutation(admin,'sharedConfiguration:configureOperator',publisher),1);
  assert.equal(await mutation(admin,'sharedConfiguration:configureOperator',publisher),1);
  const global={...template,scope:platform,requestKey:randomUUID()};assert.equal(await mutation(owner,'sharedConfiguration:save',global),1);
  assert.equal((await query(actor.other,'sharedConfiguration:selected',{scope:platform,templateId:template.templateId})).content.name,template.name);
  await assert.rejects(mutation(actor.other,'sharedConfiguration:save',{...global,expectedRevision:1,requestKey:randomUUID()}));
  log(`${mode}:organization roles, author-private receipts and admin-only platform publishing`);
  const cohort={scope,cohortId:randomUUID(),name:'Readers',purpose:'Explicit retained customer target selection.',workspaces:[workspace,second],expectedRevision:0,requestKey:randomUUID()};
  assert.equal((await query(owner,'configurationCohorts:targetReview',{scope,workspaces:cohort.workspaces})).find(target=>target.id===second).name,`Observe ${mode}`);
  assert.equal(await mutation(owner,'configurationCohorts:save',cohort),1);
  assert.equal(await mutation(owner,'configurationCohorts:save',{...cohort,workspaces:[...cohort.workspaces].reverse()}),1);
  await assert.rejects(mutation(owner,'configurationCohorts:save',{...cohort,name:'Conflicting request'}));
  for(const workspaces of [[],[workspace,workspace],Array(101).fill(workspace)])await assert.rejects(mutation(owner,'configurationCohorts:save',{...cohort,workspaces,requestKey:randomUUID()}));
  const cohortVersion=await query(owner,'configurationCohorts:version',{scope,cohortId:cohort.cohortId,contentRevision:1});
  assert.deepEqual(cohortVersion.content.workspaces,[...cohort.workspaces].sort());
  assert.equal(await mutation(owner,'configurationCohorts:save',{...cohort,workspaces:[second],expectedRevision:1,requestKey:randomUUID()}),2);
  assert.deepEqual((await query(owner,'configurationCohorts:version',{scope,cohortId:cohort.cohortId,contentRevision:1})).content,cohortVersion.content);
  assert.equal(await mutation(owner,'configurationCohorts:archive',{scope,cohortId:cohort.cohortId,archived:true,expectedRevision:2,requestKey:randomUUID()}),3);
  assert.equal(await mutation(owner,'configurationCohorts:save',cohort),1);
  assert.equal((await query(owner,'configurationCohorts:requestStatus',{scope,requestKey:cohort.requestKey})).revision,1);
  assert.equal(await query(actor.other,'configurationCohorts:requestStatus',{scope,requestKey:cohort.requestKey}),null);
  await assert.rejects(query(actor.other,'configurationCohorts:targetReview',{scope,workspaces:[second]}));
  await assert.rejects(query(actor.other,'configurationCohorts:page',{scope:platform,paginationOpts:{numItems:1,cursor:null}}));
  const foreignOrganization=await mutation(actor.other,'platform:createOrganization',{name:`Foreign ${mode}`});
  const foreign=await mutation(actor.other,'platform:createWorkspace',{organization:foreignOrganization,name:`Foreign target ${mode}`,mode:'observeOnly',privacy:'deviceOnly',remoteAllowed:false});
  await assert.rejects(mutation(owner,'configurationCohorts:save',{...cohort,cohortId:randomUUID(),workspaces:[foreign],requestKey:randomUUID()}));
  assert.deepEqual(await query(owner,'configurationCohorts:targetsView',{scope:platform,workspaces:[foreign]}),[{id:foreign,name:null,organizationName:null,available:false}]);
  await assert.rejects(query(owner,'configurationCohorts:targetReview',{scope:platform,workspaces:[foreign]}));
  log(`${mode}:cohort target-set identity, immutable membership and private foreign-organization boundaries`);
  const rolloutTemplate={...template,templateId:`rollout-${mode}`,requestKey:randomUUID()};
  await mutation(owner,'sharedConfiguration:save',rolloutTemplate);
  await mutation(owner,'sharedConfiguration:save',{...rolloutTemplate,edits:next.edits,expectedRevision:1,requestKey:randomUUID()});
  const rolloutCohort={...cohort,cohortId:randomUUID(),requestKey:randomUUID()};await mutation(owner,'configurationCohorts:save',rolloutCohort);
  const reviewArgs={scope,cohortId:rolloutCohort.cohortId,expectedCohortRevision:1,expectedCohortContentRevision:1,
    templateId:rolloutTemplate.templateId,expectedTemplateRevision:2,expectedTemplateContentRevision:2};
  const review=await query(owner,'configurationCohorts:rolloutReview',reviewArgs);
  assert.equal(review.targets.find(target=>target.workspace===second).state,'managedModeRequired');assert.equal(review.template.edits[0].value.text,'dark');
  const selected={...reviewArgs,workspace,deployment:deploymentId,expectedComputerRevision:0};
  assert.equal((await query(owner,'configurationCohorts:deploymentReview',selected)).computer,computer);
  await assert.rejects(query(owner,'configurationCohorts:deploymentReview',{...selected,workspace:foreign}));
  await assert.rejects(query(owner,'configurationCohorts:deploymentReview',{...selected,expectedComputerRevision:1}));
  await assert.rejects(query(owner,'configurationCohorts:rolloutReview',{...reviewArgs,expectedTemplateRevision:1}));
  await assert.rejects(query(actor.other,'configurationCohorts:rolloutReview',reviewArgs));
  await mutation(owner,'configurationCohorts:archive',{scope,cohortId:rolloutCohort.cohortId,expectedRevision:1,archived:true,requestKey:randomUUID()});
  await assert.rejects(query(owner,'configurationCohorts:rolloutReview',reviewArgs));
  assert.equal(await mutation(admin,'sharedConfiguration:configureOperator',{...publisher,enabled:false,expectedRevision:1,requestKey:randomUUID()}),2);
  await assert.rejects(mutation(owner,'sharedConfiguration:save',global));
  assert.equal((await query(owner,'sharedConfiguration:requestStatus',{scope:platform,requestKey:global.requestKey})).revision,1);
  await assert.rejects(query(owner,'configurationCohorts:page',{scope:platform,paginationOpts:{numItems:1,cursor:null}}));
  log(`${mode}:pinned rollout/deployment targets, stale selection refusal and live publisher revocation`);
  return {organization,scope,workspace,computer,deploymentId,template,templateReceipt:receipt,templateVersion:first.content,cohort,cohortVersion:cohortVersion.content,
    homePolicy,organizationRequest,organizationReceipt,workspaceRequest,workspaceReceipt};
}
async function retained(actor,configuration){
  assert.equal((await query(actor.owner,'identity:current')).principal,actor.principal);
  assert.deepEqual(await query(actor.owner,'identity:linkReceipt',{challenge:actor.challenge.challenge,secret:actor.linkSecret}),{state:'consumed',principal:actor.principal});
  assert.equal(await mutation(actor.owner,'sharedConfiguration:save',configuration.template),1);
  assert.deepEqual(await query(actor.owner,'sharedConfiguration:requestStatus',{scope:configuration.scope,requestKey:configuration.template.requestKey}),configuration.templateReceipt);
  assert.deepEqual((await query(actor.owner,'sharedConfiguration:version',{scope:configuration.scope,templateId:configuration.template.templateId,contentRevision:1})).content,configuration.templateVersion);
  assert.equal(await mutation(actor.owner,'configurationCohorts:save',configuration.cohort),1);
  assert.deepEqual((await query(actor.owner,'configurationCohorts:version',{scope:configuration.scope,cohortId:configuration.cohort.cohortId,contentRevision:1})).content,configuration.cohortVersion);
  assert.deepEqual(await query(actor.owner,'homeRequests:workspacePolicy',{workspace:configuration.workspace}),configuration.homePolicy);
  assert.deepEqual(await query(actor.owner,'homeRequests:creationStatus',{requestKey:configuration.organizationRequest.requestKey}),configuration.organizationReceipt);
  assert.deepEqual(await query(actor.owner,'homeRequests:creationStatus',{organization:configuration.organization,requestKey:configuration.workspaceRequest.requestKey}),configuration.workspaceReceipt);
}

try{
  if(referenceConvexDirectory){
    async function sources(directory,prefix=''){
      const result=[];
      for(const entry of await fs.readdir(directory,{withFileTypes:true})){
        const relative=prefix+entry.name,absolute=path.join(directory,entry.name);
        if(entry.isDirectory())result.push(...await sources(absolute,relative+'/'));
        else {assert.ok(entry.isFile(),'frozen reference contains only regular sources');result.push({path:relative,sha256:hash(await fs.readFile(absolute))});}
      }
      return result.sort((a,b)=>a.path.localeCompare(b.path));
    }
    summary.referenceSources=await sources(referenceConvexDirectory);
  }
  summary.backendSha256=hash(await fs.readFile(binary));summary.hostArtifacts=artifacts;summary.cliSha256=hash(await fs.readFile(nativeCli));
  key=(await run(binary,['keygen','admin-key','--instance-name',deployment,'--instance-secret',secret])).trim();
  await start('v8');
  const updated=await fetch(url+'/api/update_environment_variables',{method:'POST',headers:{'Content-Type':'application/json',Authorization:`Convex ${key}`},
    body:JSON.stringify({changes:[{name:'DBM_AUTH_ISSUER',value:issuer},{name:'DBM_AUTH_AUDIENCE',value:audience},
      {name:'DBM_AUTH_JWKS',value:jwks(pairs[0])},{name:'DBM_AUTH_ADDITIONAL_PROVIDERS',value:JSON.stringify([
        {type:'customJwt',issuer:secondaryIssuer,applicationID:audience,algorithm:'RS256',jwks:jwks(pairs[1])}])},
      {name:'DBM_BOOTSTRAP_TOKEN',value:bootstrap}]}),signal:AbortSignal.timeout(10000)});assert.ok(updated.ok);
  const cli=path.join(root,'npm-packages/convex/bin/main.js');
  const deploymentArgs=['deploy','--url',url,'--admin-key',key,'--typecheck','disable','--codegen','disable','--skip-workos-check'];
  await run(process.execPath,[cli,...deploymentArgs],project);
  const admin=client();admin.setAdminAuth(key);
  const activeSchema=async()=>{
    const schemas=await admin.query(ref('_system/frontend/getSchemas:default'),{});
    assert.equal(typeof schemas.active,'string');assert.equal(schemas.inProgress,undefined);
    return JSON.parse(schemas.active);
  };
  const originalSchema=await activeSchema();
  const oldActor=await identity('V8'),oldConfiguration=await configuration(oldActor,'V8',admin);
  const compiled=path.join(state,'sdk-prepared-graph');
  await run(process.execPath,[cli,...deploymentArgs,'--write-push-request',compiled],project);
  const graph=JSON.parse(await fs.readFile(compiled+'.json','utf8'));delete graph.adminKey;
  await fs.writeFile(compiled+'.json',JSON.stringify(graph),{mode:0o600});
  const capsuleNames=['identity.js','sharedConfiguration.js','configurationCohorts.js','homeRequests.js'];
  const beforeModules=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
  for(const file of capsuleNames){
    assert.ok(beforeModules.some(module=>module.path===file),`deployed actual source module ${file} missing`);
    graph.appDefinition.changedModules=graph.appDefinition.changedModules.filter(module=>module.path!==file);
    graph.appDefinition.unchangedModuleHashes=graph.appDefinition.unchangedModuleHashes.filter(module=>module.path!==file);
    graph.appDefinition.changedModules.push({path:file,source:await fs.readFile(path.join(nativeCapsules,file),'utf8'),
      sourceMap:null,environment:'dotNet'});
  }
  if(nativeSchemaCapsule){
    const source=await fs.readFile(nativeSchemaCapsule,'utf8');
    assert.equal(JSON.parse(source).moduleKind,'schema');
    graph.appDefinition.schema={path:'schema.js',source,sourceMap:null,environment:'dotNet'};
    summary.nativeSchema={path:'schema.js',sha256:hash(Buffer.from(source))};
  }
  const graphPath=path.join(state,'native-dbm-graph.json');await fs.writeFile(graphPath,JSON.stringify(graph),{mode:0o600});
  const activated=JSON.parse((await run(dotnet,[nativeCli,'deploy-graph',url,graphPath,'PROOF_NATIVE_ADMIN_KEY'],state,
    {PROOF_NATIVE_ADMIN_KEY:key,CONVEX_DOTNET_STATE_DIRECTORY:path.join(state,'native-client-journal')})).trim());
  assert.equal(activated.outcome,'confirmed');
  const nativeModules=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
  for(const before of beforeModules){
    const current=nativeModules.find(module=>module.path===before.path);assert.ok(current);
    if(capsuleNames.includes(before.path))assert.equal(current.environment,'dotNet');
    else if(nativeSchemaCapsule&&before.path==='schema.js'){
      assert.equal(current.environment,'dotNet');assert.equal(current.hash,summary.nativeSchema.sha256);
    }
    else assert.deepEqual(current,before,`unselected actual DBM module ${before.path} changed`);
  }
  summary.nativeCapsules=await Promise.all(capsuleNames.map(async file=>({path:file,sha256:hash(await fs.readFile(path.join(nativeCapsules,file)))})));
  log(nativeSchemaCapsule
    ?'owning native DBM deployment replaces exactly identity/shared/cohort/homeRequests and selected schema while preserving the full surrounding graph'
    :'owning native DBM deployment replaces exactly identity/shared/cohort/homeRequests modules and preserves the full surrounding graph');
  assert.deepEqual(await activeSchema(),originalSchema,'native activation changed the original owning DBM schema');
  if(nativeSchemaCapsule)log('actual DBM native schema activation preserves every owning table, validator and index');
  await retained(oldActor,oldConfiguration);log('existing signed principal, exact shared receipt and immutable cohort IDs survive V8→native');
  const nativeActor=await identity('native'),nativeConfiguration=await configuration(nativeActor,'native',admin);
  const resourceMetrics=await fetch(url+'/metrics',{signal:AbortSignal.timeout(10000)});
  assert.ok(resourceMetrics.ok,'owning native resource metrics');
  const metricsText=await resourceMetrics.text();
  const retires=metricsText.match(/^\w*native_idle_worker_resource_retire_total\s+([\d.e+-]+)$/m);
  assert.ok(retires,'native idle-worker retirement counter missing');
  summary.idleWorkerResourceRetirements=Number(retires[1]);
  assert.ok(summary.idleWorkerResourceRetirements>0,'actual native workload must exercise worker retirement before further dispatch');
  await fs.writeFile(path.join(state,'native-worker-resource-metrics.txt'),retires[0]+'\n');
  await stop();await start('native-restart');
  await retained(oldActor,oldConfiguration);await retained(nativeActor,nativeConfiguration);
  assert.deepEqual(await activeSchema(),originalSchema);
  log('actual DBM native capsule restart retains both runtime generations of principals/receipts/versions');
  await run(process.execPath,[cli,...deploymentArgs],project);
  await retained(oldActor,oldConfiguration);await retained(nativeActor,nativeConfiguration);
  assert.deepEqual(await activeSchema(),originalSchema);
  const rolledBack=(await post('/api/get_config_hashes',{adminKey:key})).moduleHashes;
  for(const file of capsuleNames)assert.equal(rolledBack.find(module=>module.path===file).environment,'isolate');
  if(nativeSchemaCapsule)assert.deepEqual(rolledBack.find(module=>module.path==='schema.js'),
    beforeModules.find(module=>module.path==='schema.js'));
  log('owning rollback restores actual DBM Fable execution with native-written data and request identity intact');
  console.log(`Actual DBM native acceptance passed. Evidence: ${path.join(state,'summary.json')}`);
}finally{
  if(reactive)await reactive.close();await stop();
  await fs.writeFile(path.join(state,'summary.json'),JSON.stringify(summary,null,2));
}
