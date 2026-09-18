import { act, fireEvent, render, screen, within } from '@testing-library/react';
import * as authApi from './api/auth';
import * as systemApi from './api/system';
import * as itemsApi from './api/items';
import * as acquisitionsApi from './api/acquisitions';
import { vi } from 'vitest';
import { App } from './App';
const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
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
vi.mock('./api/acquisitionDocuments', () => ({ getDocuments: vi.fn(async () => ({ documents: [], itemVersion: 'i1', acquisitionVersion: 'a1' })), uploadDocument: vi.fn(), changeDocument: vi.fn(), getDocumentOperation: vi.fn(), downloadDocument: vi.fn() }));
it('preserves document drafts through appearance changes and guards navigation', async () => {
  // GIVEN an authenticated item with acquisition context and an unsent document.
  window.history.replaceState(null, '', '/inventory/stone');
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  const item = { id: 'stone', name: 'Sapphire', location: null, notes: null, photo: null, version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
  // Mock the feature boundary so startup depends on React effects, not HTTP scheduling.
  vi.spyOn(systemApi, 'getSystem').mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.spyOn(authApi, 'getCurrentIdentity').mockResolvedValue({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] });
  vi.spyOn(itemsApi, 'getItem').mockResolvedValue(item);
  vi.spyOn(acquisitionsApi, 'getAcquisition').mockResolvedValue({ itemVersion: 'i1', acquisition: { id: 'fair', method: 'Purchase', source: null, year: null, month: null, day: null, notes: null, version: 'a1' } });
  await act(async () => { render(<App />); });
  const content = within(screen.getByRole('main'));
  await click(await content.findByRole('button', { name: 'Add document' }));
  fireEvent.change(content.getByLabelText('Document label'), { target: { value: 'Unsent receipt' } });
  const file = new File(['original'], 'receipt.pdf');
  fireEvent.change(content.getByLabelText('Choose document'), { target: { files: [file] } });
  // WHEN changing appearance THEN the private draft stays in memory and related edits stay locked.
  await click(screen.getByRole('button', { name: 'User menu' }));
  await click(screen.getByRole('button', { name: /^Appearance / }));
  expect(content.getByLabelText('Document label')).toHaveValue('Unsent receipt');
  expect((content.getByLabelText('Choose document') as HTMLInputElement).files?.[0]).toBe(file);
  expect(content.getByRole('button', { name: 'Change acquisition' })).toBeDisabled();
  // WHEN leaving THEN keeping the draft cancels navigation.
  await click(content.getByRole('link', { name: 'View acquisition' }));
  await screen.findByRole('dialog', { name: 'Discard changes?' });
  await click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(content.getByLabelText('Document label')).toHaveValue('Unsent receipt');
  expect(window.location.pathname).toBe('/inventory/stone');
});
