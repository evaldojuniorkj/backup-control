import test from 'node:test';
import assert from 'node:assert/strict';
import {observeCentralDate,centralNow} from './centralClock.ts';
test('dashboard time follows server even when the operator clock is one hour behind',()=>{
 const server=Date.parse('2026-11-16T23:00:00Z');const local=server-3600000;
 observeCentralDate('Mon, 16 Nov 2026 23:00:00 GMT',local,local+100);
 assert.equal(centralNow(local+100),server+50);
 assert.equal(centralNow(local+30100),server+30050);
});
test('missing or invalid clock samples do not corrupt the current reference',()=>{
 const before=centralNow(12345);observeCentralDate(null,100,200);observeCentralDate('invalid',100,200);observeCentralDate('Mon, 16 Nov 2026 23:00:00 GMT',200,100);
 assert.equal(centralNow(12345),before);
});
