import {test} from 'node:test';
import assert from 'node:assert/strict';
import {approvalPayload,eligibleExistingClients,registrationStatus} from './registration.ts';
test('approval accepts the code entered from Windows without receiving it in the registration list',()=>{
 const request={registrationId:'r',status:'Pending',expiresAt:new Date(Date.now()+60000).toISOString()};
 const code='ABCDE-12345';
 assert.throws(()=>approvalPayload(request,'wrong','existing','c',{}));
 assert.throws(()=>approvalPayload(request,'','existing','c',{}));
 assert.deepEqual(approvalPayload(request,' abcde-12345 ','existing','c',{}),{confirmationCode:'ABCDE12345',clientId:'c'});
 assert.deepEqual(approvalPayload(request,'FFFFF-FFFFF','existing','c',{}),{confirmationCode:'FFFFFFFFFF',clientId:'c'});
 assert.throws(()=>approvalPayload({...request,status:'Rejected'},code,'existing','c',{}));
 assert.throws(()=>approvalPayload({...request,status:'Expired'},code,'existing','c',{}));
 const form={name:'Empresa',slug:'empresa',database:'ATXDADOS',expectedIntervalHours:24,differentialIntervalHours:4,graceMinutes:60};
 assert.deepEqual(approvalPayload(request,code,'new','',form),{confirmationCode:'ABCDE12345',client:{...form,enabled:true}});
 assert.throws(()=>approvalPayload(request,code,'new','',{...form,slug:'../bad'}));
 assert.throws(()=>approvalPayload(request,code,'new','',{...form,differentialIntervalHours:24}));
});
test('only unbound active clients or proved existing client are eligible',()=>{
 const clients=[{id:'a',enabled:true,machineName:'',hasAgentBinding:false},{id:'b',enabled:true,machineName:'Existing',hasAgentBinding:true},{id:'c',enabled:false,machineName:'',hasAgentBinding:false}];
 assert.deepEqual(eligibleExistingClients(clients,{}).map(c=>c.id),['a']);
 assert.deepEqual(eligibleExistingClients(clients,{existingClientId:'b'}).map(c=>c.id),['a','b']);
 assert.deepEqual(eligibleExistingClients([{id:'unknown',enabled:true,machineName:''}],{}),[]);
 assert.equal(registrationStatus({status:'Expired',expiresAt:'2000-01-01T00:00:00Z'}),'Expired');
});
