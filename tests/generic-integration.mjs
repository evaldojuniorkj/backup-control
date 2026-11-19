import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
if(!process.env.TEST_URL)throw Error('TEST_URL must target disposable API/database');
const base=process.env.TEST_URL;
const env=Object.fromEntries(readFileSync(new URL('../.env',import.meta.url),'utf8').trim().split(/\r?\n/).filter(x=>x.includes('=')).map(x=>{let i=x.indexOf('=');return[x.slice(0,i),x.slice(i+1)]}));
let cookie='';
async function req(path,method='GET',body,{device,agent,team=true}={}){
 const headers={'Content-Type':'application/json','X-Requested-With':'BackupControl',...(team?{Cookie:cookie}:{}),...(device?{'X-Registration-Secret':device}:{}),...(agent?{Authorization:'Bearer '+agent.token,'X-Agent-Instance':agent.instance}:{})};
 const r=await fetch(base+'/api'+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});if(r.headers.get('set-cookie'))cookie=r.headers.get('set-cookie').split(';')[0];return r;
}
async function data(...args){const r=await req(...args);assert.ok(r.ok,args[0]+' '+r.status+' '+(!r.ok?await r.text():''));return r.json()}
const input=()=>({registrationId:crypto.randomUUID(),instanceId:crypto.randomUUID(),deviceSecret:crypto.randomUUID().replaceAll('-','')+crypto.randomUUID().replaceAll('-',''),machineName:'PC-QA',requestedName:'Generic QA'});
const register=x=>data('/agent/registrations','POST',x,{team:false});
const poll=x=>data('/agent/registrations/'+x.registrationId,'GET',undefined,{team:false,device:x.deviceSecret});
function expire(id){
 assert.match(process.env.TEST_DB_CONTAINER||'',/^bc-generic-test-db$/);assert.match(id,/^[0-9a-f-]{36}$/);
 const r=spawnSync('C:/Program Files/Docker/Docker/resources/bin/docker.exe',['exec',process.env.TEST_DB_CONTAINER,'psql','-U','qa','-d','qa','-v','ON_ERROR_STOP=1','-c',`UPDATE machine_registrations SET expires_at=now()-interval '1 minute' WHERE id='${id}'`],{encoding:'utf8'});assert.equal(r.status,0,r.stderr);
}
test('generic installer registration, approval, binding, proof and enrollment',async t=>{
 assert.equal((await req('/registrations')).status,401);assert.equal((await req('/installer/download')).status,401);
 await data('/auth/login','POST',{username:env.ADMIN_USER,password:env.ADMIN_PASSWORD});
 assert.equal((await req('/installation/settings','PUT',{centralUrl:'https://central.test',sqlServer:'localhost',sqlUsername:'sa',sqlPassword:'sql-secret-sentinel',trustServerCertificate:true})).status,204);
 const a=Buffer.from(await(await req('/installer/download')).arrayBuffer()),b=Buffer.from(await(await req('/installer/download')).arrayBuffer());assert.deepEqual(a,b);assert.equal(a.subarray(-8).toString(),'BCGEN001');const length=Number(a.readBigInt64LE(a.length-16));assert.deepEqual(JSON.parse(a.subarray(a.length-16-length,a.length-16).toString()),{centralUrl:'https://central.test'});assert.equal(a.includes(Buffer.from('sql-secret-sentinel')),false);
 const r=input();let status=await register(r);assert.equal(status.status,'Pending');assert.ok(status.code);assert.deepEqual(await poll(r),status);assert.equal('enrollmentKey' in status,false);assert.equal('sql' in status,false);
 assert.equal((await req('/agent/registrations/'+r.registrationId,'GET',undefined,{team:false,device:'b'.repeat(64)})).status,401);
 assert.equal((await req('/agent/registrations','POST',{...r,machineName:'OTHER'},{team:false})).status,409);
 const listed=(await data('/registrations')).find(x=>x.registrationId===r.registrationId);assert.equal(listed.machineName,r.machineName);assert.equal(JSON.stringify(listed).includes(r.deviceSecret),false);assert.equal('enrollmentKey' in listed,false);
 const create={name:'QA registered',slug:'generic-'+Date.now(),database:'ATXDADOS',expectedIntervalHours:8760,graceMinutes:60,differentialIntervalHours:0,enabled:true};
 assert.equal((await req('/registrations/'+r.registrationId+'/approve','POST',{client:create})).status,400);
 assert.equal((await req('/registrations/'+r.registrationId+'/approve','POST',{client:create,confirmationCode:status.code},{team:false})).status,401);
 assert.equal((await req('/registrations/'+r.registrationId+'/approve','POST',{client:create,confirmationCode:'WRONG'})).status,400);assert.equal((await poll(r)).status,'Pending');
 const approved=await data('/registrations/'+r.registrationId+'/approve','POST',{client:create,confirmationCode:status.code.toLowerCase().replace('-',' ')});t.after(()=>req('/clients/'+approved.clientId,'DELETE'));
 assert.equal(approved.status,'Approved');assert.equal((await data('/registrations/'+r.registrationId+'/approve','POST',{client:create,confirmationCode:status.code})).clientId,approved.clientId);
 status=await poll(r);assert.equal(status.status,'Approved');assert.equal(status.clientId,approved.clientId);assert.ok(status.enrollmentKey);assert.equal(status.centralUrl,'https://central.test');assert.equal('sql' in status,false);assert.deepEqual(await poll(r),status);
 const redeemed=await data('/agent/enrollment/redeem','POST',{enrollmentKey:status.enrollmentKey,instanceId:r.instanceId,machineName:r.machineName},{team:false});assert.equal(redeemed.sql.password,'sql-secret-sentinel');assert.ok(redeemed.agentToken);const agent={token:redeemed.agentToken,instance:r.instanceId};assert.equal((await data('/agent/config','GET',undefined,{team:false,agent})).clientId,approved.clientId);
 assert.equal((await req('/agent/enrollment/redeem','POST',{enrollmentKey:status.enrollmentKey,instanceId:crypto.randomUUID(),machineName:'OTHER'},{team:false})).status,409);
 const impostor={...input(),instanceId:r.instanceId};const imp=await register(impostor);assert.equal((await req('/registrations/'+impostor.registrationId+'/approve','POST',{clientId:approved.clientId,confirmationCode:imp.code})).status,409);
 const repair={...input(),instanceId:r.instanceId,existingAgentToken:agent.token};const repairStatus=await register(repair);assert.equal((await data('/registrations')).find(x=>x.registrationId===repair.registrationId).existingClientId,approved.clientId);
 await data('/registrations/'+repair.registrationId+'/approve','POST',{clientId:approved.clientId,confirmationCode:repairStatus.code});const repairKey=(await poll(repair)).enrollmentKey;const repaired=await data('/agent/enrollment/redeem','POST',{enrollmentKey:repairKey,instanceId:r.instanceId,machineName:r.machineName,existingAgentToken:agent.token},{team:false});assert.equal(repaired.agentToken,agent.token);
 assert.equal((await req('/clients/'+approved.clientId+'/status','PATCH',{enabled:false})).status,200);assert.equal((await poll(r)).status,'Rejected');
 const pausedRepair={...input(),instanceId:r.instanceId,existingAgentToken:agent.token};const pausedStatus=await register(pausedRepair);assert.equal((await req('/registrations/'+pausedRepair.registrationId+'/approve','POST',{clientId:approved.clientId,confirmationCode:pausedStatus.code})).status,409);await req('/clients/'+approved.clientId+'/status','PATCH',{enabled:true});
 const rejected=input();await register(rejected);assert.equal((await req('/registrations/'+rejected.registrationId+'/reject','POST',{})).status,204);assert.equal((await poll(rejected)).status,'Rejected');assert.equal('enrollmentKey' in await poll(rejected),false);assert.equal((await req('/registrations/'+rejected.registrationId+'/approve','POST',{client:create,confirmationCode:(await poll(rejected)).code})).status,409);
 const expired=input();const expiredStatus=await register(expired);expire(expired.registrationId);assert.equal((await poll(expired)).status,'Expired');assert.equal((await req('/registrations/'+expired.registrationId+'/approve','POST',{client:{...create,slug:create.slug+'-expired'},confirmationCode:expiredStatus.code})).status,409);
 const unbound=await data('/clients','POST',{...create,name:'Unbound',slug:create.slug+'-race'});t.after(()=>req('/clients/'+unbound.id,'DELETE'));const raceA=input(),raceB=input();const [sa,sb]=await Promise.all([register(raceA),register(raceB)]);const outcomes=await Promise.all([req('/registrations/'+raceA.registrationId+'/approve','POST',{clientId:unbound.id,confirmationCode:sa.code}),req('/registrations/'+raceB.registrationId+'/approve','POST',{clientId:unbound.id,confirmationCode:sb.code})]);assert.deepEqual(outcomes.map(x=>x.status).sort(),[200,409]);
 const adminText=JSON.stringify(await data('/registrations'));assert.equal(adminText.includes(r.deviceSecret),false);assert.equal(adminText.includes(status.enrollmentKey),false);assert.equal(adminText.includes('sql-secret-sentinel'),false);assert.equal(JSON.stringify(await data('/audit')).includes('sql-secret-sentinel'),false);
});
