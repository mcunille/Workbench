import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';
import { newGemDraft } from './features/gemReference/gemEditorDraft';

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
});
afterEach(() => { window.history.replaceState(null, '', '/'); vi.restoreAllMocks(); });
it('routes add/edit separately, guards a dirty draft and opens the saved origin-qualified detail', async () => {
  // GIVEN authenticated navigation and an effective tenant save receipt.
  window.history.replaceState(null, '', '/gem-reference');
  const entry = { ...newGemDraft('saved-id').content, origin: 'tenant', commonName: 'My ruby', species: 'Corundum', rowVersion: 'v1', layer: 'tenantEntry', sourceAssertions: [], retirement: { isRetired: false, explanation: null, redirectEntryId: null }, effectiveVersion: { sharedRowVersion: null, tenantRowVersion: 'v1' } };
  server.use(
    http.get('*/api/beta/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/beta/auth/me', () => HttpResponse.json({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf' })),
    http.get('*/api/beta/gem-reference', () => HttpResponse.json({ entries: [], nextCursor: null })),
    http.post('*/api/beta/gem-reference/tenant-entries', () => HttpResponse.json(entry)),
    http.get('*/api/beta/gem-reference/saved-id', () => HttpResponse.json(entry)),
  );
  render(<App />); fireEvent.click(await screen.findByRole('link', { name: 'Add entry' }));
  await screen.findByRole('heading', { name: 'Add tenant entry' });
  fireEvent.change(screen.getByLabelText('Common name', { exact: true }), { target: { value: 'My ruby' } });
  fireEvent.change(screen.getByLabelText('Species', { exact: true }), { target: { value: 'Corundum' } });
  // WHEN cancelling navigation THEN the user can keep the draft and the unload guard stays active.
  const unload = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(unload); expect(unload.defaultPrevented).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByLabelText('Common name', { exact: true })).toHaveValue('My ruby');
  // WHEN saving THEN routing uses the returned origin and the dirty guard clears.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('heading', { name: 'My ruby' });
  expect(window.location.pathname).toBe('/gem-reference/tenant/saved-id');
  const cleanUnload = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(cleanUnload); expect(cleanUnload.defaultPrevented).toBe(false);
  fireEvent.click(screen.getByRole('link', { name: 'Edit entry' }));
  await screen.findByRole('heading', { name: 'Edit tenant entry' });
  await waitFor(() => expect(screen.getByLabelText('Common name', { exact: true })).toHaveValue('My ruby'));
});
