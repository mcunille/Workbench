import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';
it('opens purchase orders and replaces a new draft URL after its receipt without losing current-read recovery', async () => {
  // GIVEN an authenticated user with an empty purchasing list and a confirmed save whose current read fails once.
  window.history.replaceState(null, '', '/purchase-orders');
  const draft = { title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: null, notes: null, sourceLinks: [], entries: [] };
  let reads = 0; let writes = 0;
  server.use(http.get('*/api/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/auth/me', () => HttpResponse.json({ userId: 'user', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/v2/purchase-order-drafts', () => HttpResponse.json({ items: [], nextCursor: null })),
    http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/v2/purchase-order-drafts', () => { writes++; return HttpResponse.json({ requestId: 'request', replayed: false, draftOrderId: 'saved', savedVersion: 'v1', completedAtUtc: '2026-09-12T00:00:00Z' }, { status: 201 }); }),
    http.get('*/api/v2/purchase-order-drafts/saved', () => ++reads === 1 ? new HttpResponse(null, { status: 503 }) : HttpResponse.json({ id: 'saved', poReference: 'PO-000001', supplierIsArchived: false, draft, version: 'v1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' })));
  render(<App />);
  // WHEN creating through the application's purchasing navigation.
  await screen.findByText('No draft orders yet.');
  expect(screen.getByRole('link', { name: 'Purchase orders' })).toHaveAttribute('aria-current', 'page');
  fireEvent.click(screen.getByRole('link', { name: 'New draft' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Save draft' }));
  // THEN replacing the editor URL preserves the confirmed receipt and retries only GET.
  await screen.findByText('Saved; current version could not be loaded.');
  expect(window.location.pathname).toBe('/purchase-orders/saved');
  fireEvent.click(screen.getByRole('button', { name: 'Load current draft' }));
  await waitFor(() => expect(screen.getByLabelText('Title')).not.toBeDisabled());
  expect(writes).toBe(1); expect(reads).toBe(2);
  fireEvent.click(screen.getByRole('button', { name: 'Back to purchase orders' }));
  await screen.findByText('No draft orders yet.');
});
