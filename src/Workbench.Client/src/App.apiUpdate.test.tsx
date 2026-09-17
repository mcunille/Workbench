import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

it('keeps the draft and uncertain save in place when the deployed API changes', async () => {
  // GIVEN an authenticated user editing a draft, whose first save is interrupted.
  window.history.replaceState(null, '', '/purchase-orders/new');
  let writes = 0;
  let original: unknown;
  server.use(
    http.get('*/api/beta/system', () => HttpResponse.json({ name: 'Workbench', version: 'beta', apiRevision: 'beta-1' })),
    http.get('*/api/beta/auth/me', () => HttpResponse.json({ userId: 'user', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/purchase-order-drafts/calculate', () => HttpResponse.json({ lines: [], incompleteLineCount: 0, merchandiseEstimate: null })),
    http.post('*/api/beta/purchase-order-drafts', async ({ request }) => {
      expect(request.headers.get('X-Workbench-Api-Revision')).toBe('beta-1');
      const body = await request.json();
      if (++writes === 1) { original = body; return HttpResponse.error(); }
      expect(body).toEqual(original);
      return HttpResponse.json({ code: 'api_contract_unsupported' }, { status: 409 });
    }),
  );
  render(<App />);
  fireEvent.change(await screen.findByLabelText('Title'), { target: { value: 'Keep the purchase notes' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByText('We couldn’t confirm your save.');
  // WHEN the exact save is retried after an incompatible server update.
  fireEvent.click(screen.getByRole('button', { name: 'Check and retry' }));
  // THEN a reload notice appears without discarding the draft or submitting another write.
  await screen.findByText('Workbench has been updated. Reload required.');
  expect(screen.getByLabelText('Title')).toHaveValue('Keep the purchase notes');
  expect(window.location.pathname).toBe('/purchase-orders/new');
  const retry = screen.queryByRole('button', { name: 'Check and retry' });
  if (retry) fireEvent.click(retry);
  await waitFor(() => expect(writes).toBe(2));
  expect(screen.getByLabelText('Title')).toHaveValue('Keep the purchase notes');
});
