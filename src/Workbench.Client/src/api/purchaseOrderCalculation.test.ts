import { zeroAdjustmentCalculation } from '../test/draftCalculationFixture';
import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { calculateDraft, type DraftContent } from './purchaseOrders';
import { emptyLine } from '../features/purchasing/draftLine';
it('previews the exact structured draft without a save request and protects the request', async () => {
  // GIVEN an incomplete itemization with explicit nulls and precise decimal strings.
  const draft: DraftContent = { orderDiscount: null, charges: [], title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [{ ...emptyLine('line'), quantity: '12.5000', unitOfMeasure: 'carat', priceMode: 'perUnit', price: '0.0001' }] };
  let requestBody: unknown; let token: string | null = null; let cache: RequestCache | undefined;
  const response = zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: '0.0013' }], incompleteLineCount: 0, merchandiseEstimate: '0.0013' });
  server.use(http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'preview-csrf' })), http.post('*/api/beta/purchase-order-drafts/calculate', async ({ request }) => { requestBody = await request.json(); token = request.headers.get('X-CSRF-TOKEN'); cache = request.cache; return HttpResponse.json(response); }));
  // WHEN requesting a preview THEN no receipt/requestId is invented and the server result is unmodified.
  expect(await calculateDraft(draft)).toEqual(response);
  expect(requestBody).toEqual({ draft });
  expect(token).toBe('preview-csrf');
  expect(cache).toBe('no-store');
});
