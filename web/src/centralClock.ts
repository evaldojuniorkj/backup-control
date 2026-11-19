let offset = 0;
// Use the HTTPS central response as the dashboard reference, preserving source timestamps.
export function observeCentralDate(value: string | null, startedAt: number, finishedAt: number) {
 const server = value ? Date.parse(value) : NaN;
 if (!Number.isFinite(server) || !Number.isFinite(startedAt) || !Number.isFinite(finishedAt) || finishedAt < startedAt || finishedAt-startedAt > 30000) return;
 offset = server - (startedAt + finishedAt) / 2;
}
export function centralNow(localNow = Date.now()) { return localNow + offset; }
