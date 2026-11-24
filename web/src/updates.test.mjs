import test from 'node:test';import assert from 'node:assert/strict';import {updateReason,pendingUpdate,updateRequestNotice,clientFtpPath,canDeleteClient,isOutdatedAgent,deactivationNotice,deletionResultNotice,deletionPending} from './updates.ts';import {attentionReasons} from './dashboard.ts';
import {newlyConfirmedUpdates,clientUpdateStatus} from './updates.ts';
const c={id:'fixture',name:'Teste',enabled:true,hasAgentBinding:true,remoteUpdateSupported:true,agentVersion:'0.7.0'};
test('remote requests require capability, binding, enabled state and older version',()=>{assert.equal(updateReason(c,'0.8.0',true,[]),'');for(const patch of [{enabled:false},{hasAgentBinding:false},{remoteUpdateSupported:false},{agentVersion:'0.8.0'},{agentVersion:'0.9.0'},{agentVersion:null}])assert.ok(updateReason({...c,...patch},'0.8.0',true,[]));assert.ok(updateReason(c,'0.8.0',false,[]));assert.ok(updateReason(c,'0.8.0',true,[{clientId:c.id,state:'Applying'}]));});
test('only pending and waiting can be cancelled',()=>{assert.ok(pendingUpdate('Pending'));assert.ok(pendingUpdate('Waiting'));for(const s of ['Downloading','Applying','Verifying','Succeeded','Failed','Cancelled'])assert.equal(pendingUpdate(s),false);});

test('success notifications require a tracked request confirmed by the central',()=>{
 const jobs=[
  {id:'old',state:'Succeeded',installedVersion:'0.9.2',targetVersion:'0.9.2'},
  {id:'tracked',state:'Succeeded',installedVersion:'0.9.2',targetVersion:'0.9.2'},
  {id:'checking',state:'Verifying',installedVersion:'0.9.2',targetVersion:'0.9.2'},
 ];
 assert.deepEqual(newlyConfirmedUpdates(jobs,new Set()),[]);
 assert.deepEqual(newlyConfirmedUpdates(jobs,new Set(['tracked','checking'])).map(j=>j.id),['tracked']);
});

test('client rows retain active requests and current failures while omitting resolved history',()=>{
 const priorFailure={id:'prior',clientId:c.id,state:'Failed',targetVersion:'0.9.1'};
 const failure={id:'failure',clientId:c.id,state:'Failed',targetVersion:'0.9.2'};
 const cancelled={id:'cancelled',clientId:c.id,state:'Cancelled',targetVersion:'0.9.2'};
 const active={id:'active',clientId:c.id,state:'Waiting',targetVersion:'0.9.1'};
 const completed={id:'completed',clientId:c.id,state:'Succeeded',targetVersion:'0.9.2',installedVersion:'0.9.2'};
 assert.equal(clientUpdateStatus([priorFailure],c.id,'0.9.2').job,undefined);
 assert.equal(clientUpdateStatus([failure,priorFailure],c.id,'0.9.2').job,failure);
 assert.equal(clientUpdateStatus([cancelled,failure],c.id,'0.9.2').job,undefined);
 assert.equal(clientUpdateStatus([priorFailure,active],c.id,'0.9.2').job,active);
 assert.deepEqual(clientUpdateStatus([completed,failure],c.id,'0.9.2'),{job:undefined,installedVersion:'0.9.2'});
});
test('destructive deletion requires exact typed slug without a manual agent assertion',()=>{assert.equal(canDeleteClient('teste','teste'),true);assert.equal(canDeleteClient('teste','Teste'),false);assert.equal(canDeleteClient('teste',' teste'),false);assert.equal(clientFtpPath('/backup/','teste'),'/backup/teste/');assert.equal(clientFtpPath('/','teste'),'/teste/');});
test('accepted no-change assessments prevent overdue without changing real backups',()=>{const now=Date.parse('2026-11-18T12:00:00Z');const c={enabled:true,createdAt:'2026-11-01T00:00:00Z',lastSeen:null,agentTelemetry:null,expectedIntervalHours:24,differentialIntervalHours:4,graceMinutes:15,lastFullBackupAt:'2026-11-01T00:00:00Z',lastDifferentialBackupAt:null,lastFullCheckAt:'2026-11-18T11:00:00Z',lastDifferentialCheckAt:'2026-11-18T11:00:00Z',lastFullStatus:'Succeeded',lastDifferentialStatus:'Succeeded'};assert.ok(!attentionReasons(c,now).some(r=>r.code.endsWith('-overdue')));assert.equal(c.lastFullBackupAt,'2026-11-01T00:00:00Z');assert.ok(attentionReasons({...c,lastFullCheckAt:null,lastDifferentialCheckAt:null},now).some(r=>r.code==='full-overdue'));});

test('API partial and total rejections show actual per-client errors',()=>{const clients=[{id:'a',name:'Cliente A'}];const text=updateRequestNotice({accepted:[{}],rejected:[{clientId:'a',error:'Agente incompatível'}]},clients);assert.match(text,/1 solicitação/);assert.match(text,/Cliente A: Agente incompatível/);assert.match(updateRequestNotice({accepted:[],rejected:[{clientId:'a',error:'Desativado'}]},clients),/^0 solicitação/);});

test('active clients cannot be deleted even with confirmation',()=>{assert.equal(canDeleteClient('teste','teste',true),false);assert.equal(canDeleteClient('teste','teste',false),true);});

test('only known older agent versions appear as outdated',()=>{
 for(const installed of ['0.6.0','0.7.9','0.8'])assert.equal(isOutdatedAgent(installed,'0.8.1'),true);
 for(const installed of ['0.8.1','0.8.1.0','0.9.0','0.10.0','1.0.0',null,undefined,'','invalid','0..7'])assert.equal(isOutdatedAgent(installed,'0.8.1'),false);
 assert.equal(isOutdatedAgent('0.9.0','0.10.0'),true);
 assert.equal(isOutdatedAgent('0.6.0',''),false);
});

test('pending deletion never announces removal before remote confirmation',()=>{for(const state of ['WaitingForAgent','RequiresUpgrade','Confirmed']){assert.equal(deletionPending({deactivationState:state}),true);assert.doesNotMatch(deletionResultNotice({pending:true,state}),/histórico excluídos/);}assert.match(deactivationNotice('WaitingForAgent'),/offline/);assert.match(deactivationNotice('RequiresUpgrade'),/Reinstalação inicial/);assert.match(deactivationNotice('Confirmed'),/Aguardando a exclusão/);assert.match(deletionResultNotice(undefined),/histórico excluídos/);assert.match(deletionResultNotice({}),/histórico excluídos/);assert.equal(deletionPending({status:'deletion_pending'}),true);assert.equal(deletionPending({deletionPending:true}),true);assert.equal(deletionPending({}),false);});
