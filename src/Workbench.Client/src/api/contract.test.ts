import { createApiTransport } from './contract';

afterEach(() => vi.unstubAllGlobals());

it('sends the bundled revision with generated and direct requests while preserving payloads', async () => {
  // GIVEN a request with an uncertain-save identity, exact payload, and antiforgery header.
  const transport = createApiTransport();
  const body = '{"requestId":"same-id","draft":{"notes":"original"}}';
  const send = vi.fn<typeof fetch>(async () => new Response('{}'));
  vi.stubGlobal('fetch', send);
  const input = new Request('http://localhost/api/beta/purchase-order-drafts', {
    method: 'POST', body, headers: { 'X-CSRF-TOKEN': 'csrf' },
  });
  // WHEN both generated Request objects and direct URLs pass through the transport.
  await transport.fetch(input);
  await transport.fetch(new URL('http://localhost/api/beta/items'));
  // THEN both include the bundled revision and preserve the original request.
  for (const [, init] of send.mock.calls) expect(new Headers(init?.headers).get('X-Workbench-Api-Revision')).toBe('beta-3');
  expect(new Headers(send.mock.calls[0][1]?.headers).get('X-CSRF-TOKEN')).toBe('csrf');
  expect(await input.text()).toBe(body);
});

it('latches a contract mismatch and blocks every subsequent write without resubmitting', async () => {
  // GIVEN an incompatible response and a subscribed page.
  const transport = createApiTransport();
  const notify = vi.fn();
  const unsubscribe = transport.subscribe(notify);
  const send = vi.fn<typeof fetch>(async () => Response.json({ code: 'api_contract_unsupported' }, { status: 409 }));
  vi.stubGlobal('fetch', send);
  // WHEN the response is received THEN a distinct recovery error preserves uncertain saves and notifies the page.
  await expect(transport.fetch('http://localhost/api/beta/items')).rejects.toMatchObject({ code: 'api_contract_unsupported' });
  expect(transport.getSnapshot()).toBe(true);
  expect(notify).toHaveBeenCalledTimes(1);
  // WHEN another write is attempted THEN no network request is sent, with a machine-readable failure.
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    await expect(transport.fetch('http://localhost/api/beta/items', { method })).rejects.toMatchObject({ code: 'api_contract_unsupported' });
  }
  expect(send).toHaveBeenCalledTimes(1);
  // AND reads remain available without repeatedly notifying or resetting the mismatch.
  await expect(transport.fetch('http://localhost/api/beta/items')).rejects.toMatchObject({ code: 'api_contract_unsupported' });
  expect(notify).toHaveBeenCalledTimes(1);
  expect(transport.getSnapshot()).toBe(true);
  unsubscribe();
});

it.each([new Response('not json', { status: 409 }), Response.json({ code: 'draft_version_conflict' }, { status: 409 }), Response.json({ code: 'api_contract_unsupported' })])('does not mistake ordinary responses for an incompatible deployment', async response => {
  // GIVEN a response that is not the unsupported-contract problem.
  const transport = createApiTransport();
  vi.stubGlobal('fetch', vi.fn(async () => response));
  // WHEN reading THEN existing error handling remains in control.
  await transport.fetch('http://localhost/api/beta/items');
  expect(transport.getSnapshot()).toBe(false);
});
