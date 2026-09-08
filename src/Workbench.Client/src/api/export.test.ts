// @vitest-environment node
import { vi } from 'vitest';
vi.stubGlobal('window', { location: { origin: 'http://localhost:3000' } });
vi.mock('./auth', async importOriginal => ({ ...await importOriginal<typeof import('./auth')>(), mutationHeaders: vi.fn(async () => ({ 'X-CSRF-TOKEN': 'csrf' })) }));
const { prepareExport } = await import('./export');
const filename = 'workbench-records-v1-all-20260908T123456Z.csv';
const headers = { 'Content-Type': 'text/csv; charset=utf-8', 'Content-Length': '8', 'Content-Disposition': `attachment; filename="${filename}"` };
afterEach(() => vi.unstubAllGlobals());
beforeEach(() => vi.stubGlobal('window', { location: { origin: 'http://localhost:3000' } }));

it('posts explicit scope with antiforgery and accepts only the fully read file', async () => {
  // GIVEN a complete CSV attachment.
  const fetchMock = vi.fn(async () => new Response('complete', { headers }));
  vi.stubGlobal('fetch', fetchMock);
  const signal = new AbortController().signal;
  // WHEN preparing all records THEN transport carries scope and cancellation and returns intact bytes.
  const result = await prepareExport('all', signal);
  expect(fetchMock).toHaveBeenCalledWith(expect.any(URL), expect.objectContaining({ method: 'POST', body: '{"scope":"all"}', signal, cache: 'no-store', credentials: 'same-origin', headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf' }) }));
  expect(await result?.blob.text()).toBe('complete');
  expect(result?.filename).toBe(filename);
});

it.each([401, 403, 422, 429, 503])('preserves error %s for accessible feedback', async status => {
  // GIVEN a server failure WHEN preparing THEN preserve status rather than treating it as a file.
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status, headers: { 'Content-Type': 'application/problem+json' } })));
  await expect(prepareExport('active', new AbortController().signal)).rejects.toMatchObject({ status });
});

it('recognizes an empty collection', async () => {
  // GIVEN an empty response WHEN preparing THEN no file is produced.
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })));
  await expect(prepareExport('all', new AbortController().signal)).resolves.toBeNull();
});

it.each([
  { ...headers, 'Content-Type': 'text/html' },
  { ...headers, 'Content-Type': 'text/csv-malformed' },
  { ...headers, 'Content-Length': '9' },
  { ...headers, 'Content-Length': '0' },
  { ...headers, 'Content-Length': '33554433' },
  { ...headers, 'Content-Length': '' },
  { ...headers, 'Content-Disposition': 'attachment; filename="private-name.csv"' },
  { ...headers, 'Content-Disposition': 'attachment; filename="workbench-records-v1-active-20260908T123456Z.csv"' },
])('rejects a malformed or incomplete response %#', async invalidHeaders => {
  // GIVEN a response that cannot be verified as the complete export.
  vi.stubGlobal('fetch', vi.fn(async () => new Response('complete', { headers: invalidHeaders })));
  // WHEN preparing THEN a download is never returned.
  await expect(prepareExport('all', new AbortController().signal)).rejects.toThrow();
});

it('rejects a partial-content success even when its advertised length matches', async () => {
  // GIVEN only a range of a file presented as a successful response.
  vi.stubGlobal('fetch', vi.fn(async () => new Response('complete', { status: 206, headers })));
  // WHEN preparing THEN a partial range must never be accepted as the complete export.
  await expect(prepareExport('all', new AbortController().signal)).rejects.toThrow();
});

it('rejects a body read failure and cancellation after headers', async () => {
  // GIVEN a connection that drops while delivering the body.
  const body = new ReadableStream({ start(controller) { controller.error(new Error('disconnected')); } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(body, { headers })));
  // WHEN reading THEN the partial download is rejected.
  await expect(prepareExport('all', new AbortController().signal)).rejects.toThrow('disconnected');
  // AND cancellation after headers prevents a complete-looking response from being returned.
  const controller = new AbortController();
  vi.stubGlobal('fetch', vi.fn(async () => { controller.abort(); return new Response('complete', { headers }); }));
  await expect(prepareExport('all', controller.signal)).rejects.toThrow();
});
