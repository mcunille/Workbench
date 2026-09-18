import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as authApi from './api/auth';
import * as systemApi from './api/system';
import * as itemsApi from './api/items';
import * as acquisitionsApi from './api/acquisitions';
import * as documentsApi from './api/acquisitionDocuments';
import { App } from './App';

const origin: itemsApi.ItemDetail = { id: 'stone', name: 'Blue sapphire', location: null, notes: null, photo: null, version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
const sibling = { ...origin, id: 'sibling', name: 'Green sapphire' };
const acquisition: acquisitionsApi.Acquisition = { id: 'fair', method: 'Purchase', source: 'Autumn fair', year: 2025, month: null, day: null, notes: null, version: 'a1' };

const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
beforeEach(() => {
  window.history.replaceState(null, '', '/inventory');
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  // Navigation owns these scenarios; HTTP serialization is covered by the API tests.
  vi.spyOn(systemApi, 'getSystem').mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.spyOn(authApi, 'getCurrentIdentity').mockResolvedValue({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] });
  vi.spyOn(itemsApi, 'getItems').mockResolvedValue({ items: [origin, sibling], nextCursor: 'next-page' });
  vi.spyOn(itemsApi, 'getItem').mockImplementation(async id => id === sibling.id ? sibling : origin);
  vi.spyOn(acquisitionsApi, 'getAcquisition').mockResolvedValue({ acquisition, itemVersion: 'i1' });
  vi.spyOn(acquisitionsApi, 'getSharedAcquisition').mockResolvedValue(acquisition);
  vi.spyOn(acquisitionsApi, 'getAcquisitionItems').mockResolvedValue({ items: [origin, sibling], nextCursor: null });
  vi.spyOn(acquisitionsApi, 'findAcquisitions').mockResolvedValue({ items: [acquisition], nextCursor: null });
  vi.spyOn(documentsApi, 'getDocuments').mockResolvedValue({ documents: [], itemVersion: 'i1', acquisitionVersion: 'a1' });
});

afterEach(() => {
  vi.restoreAllMocks();
  if (originalShowModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal);
  else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
  window.history.replaceState(null, '', '/');
  window.localStorage.removeItem('workbench.appearance');
});

async function click(element: HTMLElement) {
  // Complete state updates from mocked API promises before the next user action.
  await act(async () => { fireEvent.click(element); });
}

async function openCollection() {
  // Flush the mocked startup requests and their React effects before querying the collection.
  await act(async () => { render(<App />); });
  await screen.findByRole('link', { name: /Blue sapphire/ });
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'Unsent collection search' } });
  await click(screen.getByRole('button', { name: 'List' }));
  return within(screen.getByRole('main'));
}

it('preserves collection traversal through shared navigation and discards a guarded picker once', async () => {
  // GIVEN a collector has a loaded collection and separate unsent search text.
  const content = await openCollection();
  // WHEN following item to acquisition to sibling THEN the sibling gets a separate acquisition return destination.
  await click(content.getByRole('link', { name: /Blue sapphire/ }));
  await click(await content.findByRole('link', { name: 'View acquisition' }));
  await click(await content.findByRole('link', { name: 'Green sapphire' }));
  await content.findByRole('heading', { name: 'Green sapphire' });
  expect(content.getByRole('link', { name: 'Back to acquisition' })).toHaveAttribute('href', '/acquisitions/fair/from/stone');
  await click(await content.findByRole('button', { name: 'Change acquisition' }));
  fireEvent.change(await content.findByRole('searchbox', { name: 'Search acquisitions' }), { target: { value: 'My acquisition draft' } });
  await click(screen.getByRole('button', { name: 'User menu' }));
  await click(screen.getByRole('button', { name: /^Appearance / }));
  expect(content.getByRole('searchbox', { name: 'Search acquisitions' })).toHaveValue('My acquisition draft');
  await click(content.getByRole('link', { name: 'Back to acquisition' }));
  await screen.findByRole('dialog', { name: 'Discard changes?' });
  await click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(content.getByRole('searchbox', { name: 'Search acquisitions' })).toHaveValue('My acquisition draft');
  // WHEN explicitly discarding once THEN navigation completes and the original collection snapshot is retained.
  await click(content.getByRole('link', { name: 'Back to acquisition' }));
  await click(screen.getByRole('button', { name: 'Discard changes' }));
  await content.findByRole('link', { name: 'Back to piece' });
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  expect(window.location.pathname).toBe('/acquisitions/fair/from/stone');
  await click(content.getByRole('link', { name: 'Back to collection' }));
  expect(content.getByRole('searchbox')).toHaveValue('Unsent collection search');
  expect(await content.findByRole('button', { name: 'List', pressed: true })).toBeVisible();
  expect(content.getByRole('button', { name: 'Load more' })).toBeVisible();
  await waitFor(() => expect(content.getByRole('link', { name: /Blue sapphire/ })).toHaveFocus());
  expect(window.scrollTo).toHaveBeenCalled();
});

it('returns an archived sibling to the active collection it was reached from', async () => {
  // GIVEN an active collection traversal includes a shared acquisition with an archived sibling.
  const archivedSibling = { ...sibling, archivedAtUtc: '2026-01-02T00:00:00Z' };
  vi.mocked(itemsApi.getItem).mockImplementation(async id => id === sibling.id ? archivedSibling : origin);
  vi.mocked(acquisitionsApi.getAcquisitionItems).mockImplementation(async (_id, includeArchived) => ({
    items: includeArchived ? [origin, archivedSibling] : [origin], nextCursor: null,
  }));
  const content = await openCollection();
  // WHEN following the acquisition to the archived sibling and back to its acquisition.
  await click(content.getByRole('link', { name: /Blue sapphire/ }));
  await click(await content.findByRole('link', { name: 'View acquisition' }));
  await click(await content.findByRole('checkbox', { name: 'Show archived pieces' }));
  await click(await content.findByRole('link', { name: 'Green sapphire' }));
  await click(await content.findByRole('link', { name: 'View acquisition' }));
  await content.findByText(/This acquisition view is read-only/);
  // THEN the read-only view preserves the active collection as its return destination.
  expect(window.location.pathname).toBe('/acquisitions/fair/from/sibling');
  expect(content.getByRole('link', { name: 'Back to collection' })).toHaveAttribute('href', '/inventory');
  expect(content.queryByRole('link', { name: 'Back to archive' })).not.toBeInTheDocument();
  await click(content.getByRole('link', { name: 'Back to collection' }));
  expect(content.getByRole('searchbox')).toHaveValue('Unsent collection search');
  expect(await content.findByRole('button', { name: 'List', pressed: true })).toBeVisible();
});
