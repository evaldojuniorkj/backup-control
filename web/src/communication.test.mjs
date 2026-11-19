import test from 'node:test';
import assert from 'node:assert/strict';
import { communicationReasons, agentDate, reportTimeLabel } from './communication.ts';
test('clock and pending-result warnings share canonical client state',()=>{
 const c={telemetryReceivedAt:'2026-11-16T20:00:00Z',communication:{clockSkewSeconds:3600,pendingResults:1,lastResultError:'HTTP 400',requiresAgentUpdate:true}};
 const reasons=communicationReasons(c);assert.ok(reasons.some(r=>r.code==='clock-skew'));assert.ok(reasons.some(r=>r.code==='pending-results'));assert.ok(reasons.some(r=>r.code==='result-error'));assert.ok(reasons.some(r=>r.code==='agent-update'));
 assert.equal(agentDate('2026-11-16T21:00:00Z',c),'2026-11-16T20:00:00.000Z');
});
test('small offsets do not rewrite reported schedule and credential failures are not echoed',()=>{
 assert.equal(agentDate('2026-11-16T20:00:00Z',{communication:{clockSkewSeconds:15}}),'2026-11-16T20:00:00Z');
 const reasons=communicationReasons({communication:{pendingResults:0,sqlConfigStatus:'Failed',sqlConfigError:'Password=secret'}});assert.ok(reasons.some(r=>r.code==='sql-config'));assert.equal(JSON.stringify(reasons).includes('secret'),false);
 assert.match(reportTimeLabel('ClockAdjusted'),/corrigido/);assert.match(reportTimeLabel('ReceiptEstimated'),/recebimento/);
});
test('clock warnings retain sample provenance and mark stale observations',()=>{
 const sample='2026-11-16T20:00:00Z';const c={telemetryReceivedAt:'2026-11-16T21:00:00Z',communication:{clockSkewSeconds:3600,clockSampledAt:sample}};
 const reason=communicationReasons(c,Date.parse('2026-11-16T21:00:00Z')).find(r=>r.code==='clock-skew');
 assert.equal(reason.since,sample);assert.match(reason.message,/Último desvio observado/);
 assert.match(reportTimeLabel('ClockAdjusted'),/estimado/);
});
