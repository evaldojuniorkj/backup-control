import { test } from 'node:test';
import assert from 'node:assert/strict';
import { centralOrigin, installerDownloadPath } from './installation.ts';
test('central origin rejects non HTTPS, credentials and non-origin URLs', () => {
  for (const input of ['http://localhost:8080', 'https://user:pass@example.com', 'https://example.com/api', 'https://example.com/?key=secret', 'https://example.com/#part', 'invalid']) assert.throws(() => centralOrigin(input));
  assert.equal(centralOrigin(' https://backup.example.com:8443/ '), 'https://backup.example.com:8443');
});
test('installer download is restricted to exact authenticated route on same origin', () => {
  const route = '/api/clients/client-id/installers/enrollment-id/download';
  const safe = (input) => installerDownloadPath(input, 'https://panel.example.com', 'client-id', 'enrollment-id');
  assert.equal(safe(route), route);
  assert.equal(safe('https://panel.example.com'+route), route);
  for (const input of ['https://evil.example'+route, '//evil.example'+route, route+'?key=secret', route+'#x', route.replace('client-id','other-client'), '/api/auth/logout', 'javascript:alert(1)']) assert.equal(safe(input), '');
});
