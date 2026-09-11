import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { vi } from 'vitest';
import { App } from './App';
import { server } from './test/server';
vi.mock('./api/acquisitionDocuments', () => ({ getDocuments: vi.fn(async () => ({ documents: [], itemVersion: 'i1', acquisitionVersion: 'a1' })), uploadDocument: vi.fn(), changeDocument: vi.fn(), getDocumentOperation: vi.fn(), downloadDocument: vi.fn() }));
it('preserves document drafts through appearance changes and guards navigation', async () => {
  // GIVEN an authenticated item with acquisition context and an unsent document.
  window.history.replaceState(null, '', '/inventory/stone');
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
  const item = { id: 'stone', name: 'Sapphire', location: null, notes: null, photo: null, version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
  server.use(
    http.get('*/api/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/auth/me', () => HttpResponse.json({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/items/stone', () => HttpResponse.json(item)),
    http.get('*/api/items/stone/acquisition', () => HttpResponse.json({ itemVersion: 'i1', acquisition: { id: 'fair', method: 'Purchase', source: null, year: null, month: null, day: null, notes: null, version: 'a1' } })),
  );
  render(<App />);
  fireEvent.click(await screen.findByRole('button', { name: 'Add document' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Unsent receipt' } });
  const file = new File(['original'], 'receipt.pdf');
  fireEvent.change(screen.getByLabelText('Choose document'), { target: { files: [file] } });
  // WHEN changing appearance THEN the private draft stays in memory and related edits stay locked.
  fireEvent.click(screen.getByRole('button', { name: 'User menu' }));
  fireEvent.click(screen.getByRole('button', { name: /^Appearance / }));
  expect(screen.getByLabelText('Document label')).toHaveValue('Unsent receipt');
  expect((screen.getByLabelText('Choose document') as HTMLInputElement).files?.[0]).toBe(file);
  expect(screen.getByRole('button', { name: 'Change acquisition' })).toBeDisabled();
  // WHEN leaving THEN keeping the draft cancels navigation.
  fireEvent.click(screen.getByRole('link', { name: 'View acquisition' }));
  await screen.findByRole('dialog', { name: 'Discard changes?' });
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByLabelText('Document label')).toHaveValue('Unsent receipt');
  expect(window.location.pathname).toBe('/inventory/stone');
  window.history.replaceState(null, '', '/');
});
