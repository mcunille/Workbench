import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { createDraft, updateDraft, getDraft, getDrafts, deleteDraft, DraftError } from './purchaseOrders';
const draft = { title: null, supplierName: null, currency: 'USD', notes: null, sourceLinks: [], entries: [{ id: 'entry', description: null, notes: null, sourceLink: null, indicativePrice: '0.0000' }] };
it('sends exact decimal strings and a protected full replacement, returning a compact receipt', async () => {
  // GIVEN a full replacement with a zero reference price and a current version.
  const body = { requestId: 'request', expectedVersion: 'token', draft };
  const receipt = { requestId: 'request', replayed: false, draftOrderId: 'draft', savedVersion: 'next', completedAtUtc: '2026-09-12T00:00:00Z' };
  let received: unknown; let csrf: string | null = null;
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf-test' })), http.put('*/api/purchase-order-drafts/draft', async ({ request }) => { received = await request.json(); csrf = request.headers.get('X-CSRF-TOKEN'); return HttpResponse.json(receipt); }));
  // WHEN updating THEN exact input and protection are sent, and the receipt is not reinterpreted as content.
  expect(await updateDraft('draft', body)).toEqual(receipt); expect(received).toEqual(body); expect(csrf).toBe('csrf-test');
});
it('encodes opaque cursors and makes private uncached detail and page reads', async () => {
  // GIVEN an opaque cursor with reserved characters.
  const requests: Request[] = [];
  server.use(http.get('*/api/purchase-order-drafts', ({ request }) => { requests.push(request); return HttpResponse.json({ items: [], nextCursor: null }); }), http.get('*/api/purchase-order-drafts/draft', ({ request }) => { requests.push(request); return HttpResponse.json({ id: 'draft', draft }); }));
  // WHEN reading the page and current draft THEN private data cannot use the browser cache.
  await getDrafts('a+b/='); await getDraft('draft');
  expect(new URL(requests[0].url).searchParams.get('cursor')).toBe('a+b/=');
  expect(requests.every(request => request.cache === 'no-store' && request.credentials === 'same-origin')).toBe(true);
});
it('preserves validation paths and distinguishes request collisions from stale versions', async () => {
  // GIVEN the API returns authoritative purchasing problem details.
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf-test' })), http.post('*/api/purchase-order-drafts', () => HttpResponse.json({ code: 'draft_validation_failed', errors: { 'draft.currency': ['Required with a price.'] } }, { status: 400 })), http.put('*/api/purchase-order-drafts/draft', () => HttpResponse.json({ code: 'draft_request_conflict' }, { status: 409 })));
  // WHEN saving THEN recovery has the exact status, code and field paths.
  await expect(createDraft({ requestId: 'request', draft })).rejects.toMatchObject({ status: 400, code: 'draft_validation_failed', errors: { 'draft.currency': ['Required with a price.'] } });
  await expect(updateDraft('draft', { requestId: 'request', expectedVersion: 'token', draft })).rejects.toEqual(expect.objectContaining({ status: 409, code: 'draft_request_conflict' }));
  expect(new DraftError(400)).toBeInstanceOf(Error);
});

it('sends protected deletion with an exact request and version and returns its receipt', async () => {
  // GIVEN a loaded draft version and a unique deletion request.
  const body = { requestId: 'delete-request', expectedVersion: 'version' };
  const receipt = { requestId: 'delete-request', replayed: true, draftOrderId: 'draft', savedVersion: 'deleted', completedAtUtc: '2026-09-12T00:00:00Z' };
  let received: unknown; let csrf: string | null = null;
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf-test' })), http.delete('*/api/purchase-order-drafts/draft', async ({ request }) => {
    received = await request.json(); csrf = request.headers.get('X-CSRF-TOKEN'); return HttpResponse.json(receipt);
  }));
  // WHEN deleting THEN the body and antiforgery token are sent and a replay remains successful.
  expect(await deleteDraft('draft', body)).toEqual(receipt);
  expect(received).toEqual(body); expect(csrf).toBe('csrf-test');
});
