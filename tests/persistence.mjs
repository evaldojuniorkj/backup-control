import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
const env=Object.fromEntries(readFileSync('.env','utf8').trim().split(/\r?\n/).map(x=>{const i=x.indexOf('=');return[x.slice(0,i),x.slice(i+1)];}));
const url='http://localhost:5180/api';
const headers={'Content-Type':'application/json','X-Requested-With':'BackupControl'};
const login=await fetch(url+'/auth/login',{method:'POST',headers,body:JSON.stringify({username:env.ADMIN_USER,password:env.ADMIN_PASSWORD})});
assert.equal(login.status,200);
headers.Cookie=login.headers.get('set-cookie').split(';')[0];
const created=await fetch(url+'/clients',{method:'POST',headers,body:JSON.stringify({name:'Teste de persistência',slug:'persist-'+Date.now(),database:'Teste',expectedIntervalHours:24,graceMinutes:60})});
assert.equal(created.status,201);
const client=await created.json();
const agentHeaders={Authorization:`Bearer ${client.agentToken}`,'X-Agent-Instance':crypto.randomUUID()};
const config=await (await fetch(url+'/agent/config',{headers:agentHeaders})).json();
try {
  execFileSync(process.env.DOCKER_BIN||'docker',['compose','restart','db','api','web'],{stdio:'pipe'});
  let ready=false;
  for(let i=0;i<50;i++) {
    try{if((await fetch(url+'/health')).ok){ready=true;break;}}catch{}
    await new Promise(r=>setTimeout(r,500));
  }
  assert.ok(ready,'Serviços não ficaram prontos após reinício');
  const list=await fetch(url+'/clients',{headers});
  assert.equal(list.status,200,'Cookie deve sobreviver ao reinício');
  assert.ok((await list.json()).some(c=>c.id===client.id),'Cadastro deve persistir');
  const after=await fetch(url+'/agent/config',{headers:agentHeaders});
  assert.equal(after.status,200,'Token do agente deve persistir');
  const afterConfig=await after.json();
  assert.deepEqual(afterConfig,config,'Configuração e senha protegida devem sobreviver ao reinício');
  console.log('PASS: PostgreSQL, token, sessão e chaves da configuração preservados após reinício dos três serviços.');
} finally {
  await fetch(url+'/clients/'+client.id,{method:'DELETE',headers});
}
