import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

it('keeps exact uncertain retries and ordinary validation recovery without a deployment lock', async () => {
  // GIVEN an authenticated user editing a draft, whose first save is interrupted.
  window.history.replaceState(null, '', '/purchase-orders/new');
  let writes = 0;
  let original: unknown;
  server.use(
    http.get('*/api/beta/system', () => HttpResponse.json({ name: 'Workbench', version: 'beta' })),
    http.get('*/api/beta/auth/me', () => HttpResponse.json({ userId: 'user', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/purchase-order-drafts/calculate', () => HttpResponse.json({ lines: [], incompleteLineCount: 0, merchandiseEstimate: null })),
    http.post('*/api/beta/purchase-order-drafts', async ({ request }) => {
      expect(request.headers.has('X-Workbench-Api-Revision')).toBe(false);
      const body = await request.json();
      if (++writes === 1) { original = body; return HttpResponse.error(); }
      expect(body).toEqual(original);
      return HttpResponse.json({ code: 'draft_validation_failed', errors: { 'draft.title': ['Review this title.'] } }, { status: 400 });
    }),
  );
  render(<App />);
  fireEvent.change(await screen.findByLabelText('Title'), { target: { value: 'Keep the purchase notes' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await screen.findByText('We couldn’t confirm your save.');
  // WHEN the exact save is retried and ordinary endpoint validation rejects it.
  fireEvent.click(screen.getByRole('button', { name: 'Check and retry' }));
  // THEN input remains editable and no global reload notice replaces endpoint recovery.
  await screen.findAllByText('Review this title.');
  expect(screen.queryByText('Workbench has been updated. Reload required.')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Title')).toBeEnabled();
  expect(screen.getByLabelText('Title')).toHaveValue('Keep the purchase notes');
  expect(window.location.pathname).toBe('/purchase-orders/new');
  await waitFor(() => expect(writes).toBe(2));
  expect(screen.getByLabelText('Title')).toHaveValue('Keep the purchase notes');
});
