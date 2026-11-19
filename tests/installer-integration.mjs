import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {join} from 'node:path';
const env=Object.fromEntries(readFileSync(new URL('../.env',import.meta.url),'utf8').trim().split(/\r?\n/).filter(x=>x.includes('=')).map(x=>{let i=x.indexOf('=');return [x.slice(0,i),x.slice(i+1)];}));
if(!process.env.TEST_URL)throw new Error('TEST_URL must point to an isolated disposable API; this test changes shared installation defaults.');
const base=process.env.TEST_URL;let cookie='';
async function req(path,method='GET',body,headers={}){const r=await fetch(base+'/api'+path,{method,headers:{'Content-Type':'application/json','X-Requested-With':'BackupControl',...(path==='/agent/enrollment/redeem'?{}:{Cookie:cookie}),...headers},body:body===undefined?undefined:JSON.stringify(body)});if(r.headers.get('set-cookie'))cookie=r.headers.get('set-cookie').split(';')[0];return r;}
test('shared defaults and enrollment isolate credentials and preserve retry identity',async t=>{
 assert.equal((await req('/installation/settings')).status,401);
 assert.equal((await req('/auth/login','POST',{username:env.ADMIN_USER,password:env.ADMIN_PASSWORD})).status,200);
 const settings=await req('/installation/settings');assert.equal(settings.status,200);
 assert.equal('sqlPassword' in await settings.json(),false);
 assert.equal((await req('/installation/settings','PUT',{centralUrl:'https://installer.test',sqlServer:'localhost',sqlUsername:'sa',sqlPassword:'integration-secret-only',trustServerCertificate:true,sqlServiceIdentity:'',backupDirectory:''})).status,204);
 const c=await (await req('/clients','POST',{name:'Installer test',slug:'install-'+Date.now(),database:'Test',expectedIntervalHours:24,graceMinutes:60,enabled:true})).json();t.after(()=>req('/clients/'+c.id,'DELETE'));
 const issued=await req(`/clients/${c.id}/installer`,'POST');assert.equal(issued.status,200);const e=await issued.json();
 const download=await req(`/clients/${c.id}/installers/${e.id}/download`);assert.equal(download.status,200);
 const bytes=Buffer.from(await download.arrayBuffer());assert.equal(bytes.subarray(-8).toString(),'BCSETUP1');const n=Number(bytes.readBigInt64LE(bytes.length-16));const payload=JSON.parse(bytes.subarray(bytes.length-16-n,bytes.length-16));assert.equal('sqlPassword' in payload,false);
 assert.equal((await req('/installation/settings','PUT',{centralUrl:'https://installer.test',sqlServer:'localhost',sqlUsername:'sa',sqlPassword:'',trustServerCertificate:true,sqlServiceIdentity:'',backupDirectory:''})).status,204);
 const identity=crypto.randomUUID();const redeem=()=>req('/agent/enrollment/redeem','POST',{enrollmentKey:payload.enrollmentKey,instanceId:identity,machineName:'InstallerTest'});
 assert.equal((await req('/agent/enrollment/redeem','POST',{enrollmentKey:payload.enrollmentKey,instanceId:identity,machineName:'InstallerTest',existingAgentToken:c.agentToken})).status,409);
 const first=await redeem();assert.equal(first.status,200);assert.equal(first.headers.get('cache-control'),'no-store');const reply=await first.json();assert.equal(reply.sql.password,'integration-secret-only');assert.deepEqual(await (await redeem()).json(),reply);
 assert.equal((await req('/agent/enrollment/redeem','POST',{enrollmentKey:payload.enrollmentKey,instanceId:crypto.randomUUID(),machineName:'Other'})).status,409);
 assert.equal((await req('/installation/settings','PUT',{centralUrl:'https://installer.test',sqlServer:'changed',sqlUsername:'sa',sqlPassword:'changed-secret',trustServerCertificate:true,sqlServiceIdentity:'',backupDirectory:''})).status,204);
 assert.deepEqual(await (await redeem()).json(),reply);
 assert.equal((await req('/agent/package')).status,401);
 const pinnedPath=new URL(reply.package.url).pathname.replace('/api','');assert.equal(pinnedPath,'/agent/package/'+reply.package.sha256);
 const pkg=await req(pinnedPath,'GET',undefined,{Authorization:'Bearer '+reply.agentToken,'X-Agent-Instance':identity});assert.equal(pkg.status,200);assert.equal(Buffer.from(await crypto.subtle.digest('SHA-256',await pkg.arrayBuffer())).toString('hex'),reply.package.sha256);
 if(!process.env.TEST_DISTRIBUTION_DIR)throw new Error('TEST_DISTRIBUTION_DIR must reference the isolated mounted test distribution.');
 const latest=join(process.env.TEST_DISTRIBUTION_DIR,'backup-control-agent-win-x64.zip');const original=readFileSync(latest);
 try {
  writeFileSync(latest,Buffer.from('replacement package without a publisher snapshot'));
  assert.deepEqual(await (await redeem()).json(),reply);
  const priorPackage=await req(pinnedPath,'GET',undefined,{Authorization:'Bearer '+reply.agentToken,'X-Agent-Instance':identity});assert.equal(priorPackage.status,200);
  assert.equal(Buffer.from(await crypto.subtle.digest('SHA-256',await priorPackage.arrayBuffer())).toString('hex'),reply.package.sha256);
  assert.equal((await req(`/clients/${c.id}/installer`,'POST')).status,503);
 } finally {writeFileSync(latest,original);}
 const racing=await (await req('/clients','POST',{name:'Enrollment race',slug:'race-'+Date.now(),database:'Test',expectedIntervalHours:24,graceMinutes:60,enabled:true})).json();t.after(()=>req('/clients/'+racing.id,'DELETE'));
 const re=await (await req(`/clients/${racing.id}/installer`,'POST')).json();const rb=Buffer.from(await (await req(re.downloadUrl.replace('/api',''))).arrayBuffer());const rn=Number(rb.readBigInt64LE(rb.length-16));const rk=JSON.parse(rb.subarray(rb.length-16-rn,rb.length-16)).enrollmentKey;
 const identities=[crypto.randomUUID(),crypto.randomUUID()];
 const simultaneous=await Promise.all(identities.map(instanceId=>req('/agent/enrollment/redeem','POST',{enrollmentKey:rk,instanceId,machineName:'Race'})));
 assert.deepEqual(simultaneous.map(response=>response.status).sort(),[200,409]);
 const winnerIndex=simultaneous.findIndex(response=>response.status===200);const winningReply=await simultaneous[winnerIndex].json();
 assert.deepEqual(await (await req('/agent/enrollment/redeem','POST',{enrollmentKey:rk,instanceId:identities[winnerIndex],machineName:'Race'})).json(),winningReply);
 const repair=await (await req(`/clients/${c.id}/installer`,'POST')).json();const repairBytes=Buffer.from(await (await req(repair.downloadUrl.replace('/api',''))).arrayBuffer());const repairLength=Number(repairBytes.readBigInt64LE(repairBytes.length-16));const repairKey=JSON.parse(repairBytes.subarray(repairBytes.length-16-repairLength,repairBytes.length-16)).enrollmentKey;
 const repairRequest=existingAgentToken=>req('/agent/enrollment/redeem','POST',{enrollmentKey:repairKey,instanceId:identity,machineName:'InstallerTest',existingAgentToken});
 assert.equal((await repairRequest('wrong-token')).status,409);
 const repaired=await repairRequest(reply.agentToken);assert.equal(repaired.status,200);const repairedReply=await repaired.json();assert.equal(repairedReply.agentToken,reply.agentToken);
 assert.deepEqual(await (await repairRequest(reply.agentToken)).json(),repairedReply);
 assert.equal((await req(pinnedPath,'GET',undefined,{Authorization:'Bearer '+reply.agentToken,'X-Agent-Instance':identity})).status,200);
 const newer=await (await req(`/clients/${c.id}/installer`,'POST')).json();const nb=Buffer.from(await (await req(newer.downloadUrl.replace('/api',''))).arrayBuffer());const nn=Number(nb.readBigInt64LE(nb.length-16));const nk=JSON.parse(nb.subarray(nb.length-16-nn,nb.length-16)).enrollmentKey;
 const newReply=await (await req('/agent/enrollment/redeem','POST',{enrollmentKey:nk,instanceId:identity,machineName:'InstallerTest'})).json();assert.notEqual(newReply.agentToken,reply.agentToken);assert.equal((await redeem()).status,409);
 await req(`/clients/${c.id}/installers/${newer.id}/revoke`,'POST');assert.equal((await req(new URL(newReply.package.url).pathname.replace('/api',''),'GET',undefined,{Authorization:'Bearer '+newReply.agentToken,'X-Agent-Instance':identity})).status,200);
 await req(`/clients/${c.id}/installers/${e.id}/revoke`,'POST');assert.equal((await req(`/clients/${c.id}/installers/${e.id}/download`)).status,410);
 await req(`/clients/${c.id}/status`,'PATCH',{enabled:false});assert.equal((await req(`/clients/${c.id}/installer`,'POST')).status,409);
});






