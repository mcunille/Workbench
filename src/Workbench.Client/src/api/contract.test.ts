import { apiFetch } from './contract';

afterEach(() => vi.unstubAllGlobals());

it('preserves request identity and antiforgery without negotiating a beta revision', async () => {
  // GIVEN an exact retry payload and antiforgery token.
  const body = '{"requestId":"same-id","draft":{"notes":"original"}}';
  const send = vi.fn<typeof fetch>(async () => new Response('{}'));
  vi.stubGlobal('fetch', send);
  const input = new Request('http://localhost/api/beta/purchase-order-drafts', { method: 'POST', body, headers: { 'X-CSRF-TOKEN': 'csrf' } });
  // WHEN generated and direct calls use the shared transport.
  await apiFetch(input);
  await apiFetch(new URL('http://localhost/api/beta/items'));
  // THEN no revision is added and the original body and token are unchanged.
  for (const [request, init] of send.mock.calls) {
    const headers = new Headers(init?.headers ?? (request instanceof Request ? request.headers : undefined));
    expect(headers.has('X-Workbench-Api-Revision')).toBe(false);
  }
  expect(input.headers.get('X-CSRF-TOKEN')).toBe('csrf');
  expect(await input.text()).toBe(body);
});

it('returns endpoint conflicts without freezing subsequent writes', async () => {
  // GIVEN a retired-route conflict followed by a normal write.
  const conflict = Response.json({ code: 'api_contract_unsupported' }, { status: 409 });
  const send = vi.fn<typeof fetch>().mockResolvedValueOnce(conflict).mockResolvedValueOnce(new Response('{}'));
  vi.stubGlobal('fetch', send);
  // WHEN the endpoint rejects a request THEN its caller handles the response normally.
  expect(await apiFetch('http://localhost/api/retired')).toBe(conflict);
  // AND the response does not establish a global write lock.
  expect((await apiFetch('http://localhost/api/beta/items', { method: 'POST' })).ok).toBe(true);
  expect(send).toHaveBeenCalledTimes(2);
});
