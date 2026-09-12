import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from './App';
import * as authApi from './api/auth';
import * as systemApi from './api/system';
import * as purchasingApi from './api/purchaseOrders';

const draft = { title: 'First draft', supplierName: null, currency: null, notes: null, sourceLinks: [], entries: [] };
const first: purchasingApi.DraftOrder = { id: 'first', draft, version: 'v1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
const second: purchasingApi.DraftOrder = { ...first, id: 'second', draft: { ...draft, title: 'Second draft', notes: 'Saved second notes' } };
const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');

beforeEach(() => {
  // Navigation is the feature boundary under test; transport has separate coverage.
  window.history.replaceState(null, '', '/purchase-orders/first');
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.spyOn(systemApi, 'getSystem').mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.spyOn(authApi, 'getCurrentIdentity').mockResolvedValue({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] });
  vi.spyOn(purchasingApi, 'getDrafts').mockResolvedValue({ items: [first, second].map(value => ({ id: value.id, title: value.draft.title, supplierName: null, updatedAtUtc: value.updatedAtUtc })), nextCursor: null });
  vi.spyOn(purchasingApi, 'getDraft').mockImplementation(async id => id === second.id ? second : first);
});
afterEach(() => {
  vi.restoreAllMocks();
  if (originalShowModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal);
  else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
  window.history.replaceState(null, '', '/');
  window.localStorage.removeItem('workbench.appearance');
});
async function click(element: HTMLElement) {
  await act(async () => { fireEvent.click(element); });
}
async function go(delta: number) {
  await act(async () => {
    const popped = new Promise<void>(resolve => window.addEventListener('popstate', () => resolve(), { once: true }));
    window.history.go(delta);
    await popped;
  });
}
it('keeps unsaved input and navigation protection when history jumps to another entry for the same draft', async () => {
  // GIVEN the same saved draft is present on both sides of a list entry in browser history.
  await act(async () => { render(<App />); });
  await waitFor(() => expect(screen.getByLabelText('Title')).toHaveValue('First draft'));
  await click(screen.getByRole('button', { name: 'Back to purchase orders' }));
  await click(await screen.findByRole('link', { name: /First draft/ }));
  await waitFor(() => expect(screen.getByLabelText('Title')).toHaveValue('First draft'));
  fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'Unsaved supplier questions' } });
  // WHEN jumping over the list to the older history entry for this same draft.
  await go(-2);
  // THEN the mounted editor keeps its input, does not refetch, and still guards real departures.
  expect(window.location.pathname).toBe('/purchase-orders/first');
  expect(screen.getByLabelText('Notes')).toHaveValue('Unsaved supplier questions');
  expect(purchasingApi.getDraft).toHaveBeenCalledTimes(2);
  const unload = new Event('beforeunload', { cancelable: true });
  expect(window.dispatchEvent(unload)).toBe(false);
  await click(screen.getByRole('button', { name: 'Back to purchase orders' }));
  await screen.findByRole('dialog', { name: 'Discard changes?' });
  await click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByLabelText('Notes')).toHaveValue('Unsaved supplier questions');
});
it('loads the different draft when history jumps directly between editor routes', async () => {
  // GIVEN history holds a second draft, the list, and then a first draft.
  window.history.replaceState(null, '', '/purchase-orders/second');
  await act(async () => { render(<App />); });
  await waitFor(() => expect(screen.getByLabelText('Title')).toHaveValue('Second draft'));
  await click(screen.getByRole('button', { name: 'Back to purchase orders' }));
  await click(await screen.findByRole('link', { name: /First draft/ }));
  await waitFor(() => expect(screen.getByLabelText('Title')).toHaveValue('First draft'));
  // WHEN jumping directly back to the different saved draft.
  await go(-2);
  // THEN its content replaces the prior editor rather than reusing the wrong draft's state.
  expect(window.location.pathname).toBe('/purchase-orders/second');
  expect(screen.getByLabelText('Title')).toHaveValue('Second draft');
  expect(screen.getByLabelText('Notes')).toHaveValue('Saved second notes');
  expect(purchasingApi.getDraft).toHaveBeenLastCalledWith('second');
});
