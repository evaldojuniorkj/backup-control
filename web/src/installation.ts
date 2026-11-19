export function centralOrigin(input: string): string {
  const url = new URL(input.trim());
  if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash) throw new Error('Invalid HTTPS origin');
  return url.origin;
}
export function installerDownloadPath(input: string, origin: string, clientId: string, enrollmentId: string): string {
  try {
    const url = new URL(input, origin);
    const expected = `/api/clients/${encodeURIComponent(clientId)}/installers/${encodeURIComponent(enrollmentId)}/download`;
    return url.origin === origin && url.pathname === expected && !url.search && !url.hash && !url.username && !url.password ? url.pathname : '';
  } catch { return ''; }
}
