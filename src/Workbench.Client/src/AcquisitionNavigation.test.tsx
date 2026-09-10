import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

it('preserves collection traversal through shared navigation and discards a guarded picker once', async () => {
  // GIVEN a collector has a loaded collection and separate unsent search text.
  window.history.replaceState(null, '', '/inventory');
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
  const origin = { id: 'stone', name: 'Blue sapphire', location: null, notes: null, photo: null, version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
  const sibling = { ...origin, id: 'sibling', name: 'Green sapphire' };
  const acquisition = { id: 'fair', method: 'Purchase', source: 'Autumn fair', year: 2025, month: null, day: null, notes: null, version: 'a1' };
  server.use(
    http.get('*/api/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/auth/me', () => HttpResponse.json({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] })),
    http.get('*/api/items', () => HttpResponse.json({ items: [origin, sibling], nextCursor: 'next-page' })),
    http.get('*/api/items/:id', ({ params }) => HttpResponse.json(params.id === 'sibling' ? sibling : origin)),
    http.get('*/api/items/:id/acquisition', () => HttpResponse.json({ acquisition, itemVersion: 'i1' })),
    http.get('*/api/acquisitions/fair', () => HttpResponse.json(acquisition)),
    http.get('*/api/acquisitions/fair/items', () => HttpResponse.json({ items: [origin, sibling], nextCursor: null })),
    http.get('*/api/acquisitions', () => HttpResponse.json({ items: [acquisition], nextCursor: null })),
  );
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  render(<App />);
  await screen.findByRole('link', { name: /Blue sapphire/ });
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'Unsent collection search' } });
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  // WHEN following item to acquisition to sibling THEN the sibling gets a separate acquisition return destination.
  fireEvent.click(screen.getByRole('link', { name: /Blue sapphire/ }));
  fireEvent.click(await screen.findByRole('link', { name: 'View acquisition' }));
  fireEvent.click(await screen.findByRole('link', { name: 'Green sapphire' }));
  await screen.findByRole('heading', { name: 'Green sapphire' });
  expect(screen.getByRole('link', { name: 'Back to acquisition' })).toHaveAttribute('href', '/acquisitions/fair/from/stone');
  fireEvent.click(await screen.findByRole('button', { name: 'Change acquisition' }));
  fireEvent.change(await screen.findByRole('searchbox', { name: 'Search acquisitions' }), { target: { value: 'My acquisition draft' } });
  fireEvent.click(screen.getByRole('button', { name: 'User menu' }));
  fireEvent.click(screen.getByRole('button', { name: /^Appearance / }));
  expect(screen.getByRole('searchbox', { name: 'Search acquisitions' })).toHaveValue('My acquisition draft');
  fireEvent.click(screen.getByRole('link', { name: 'Back to acquisition' }));
  await screen.findByRole('dialog', { name: 'Discard changes?' });
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByRole('searchbox', { name: 'Search acquisitions' })).toHaveValue('My acquisition draft');
  // WHEN explicitly discarding once THEN navigation completes and the original collection snapshot is retained.
  fireEvent.click(screen.getByRole('link', { name: 'Back to acquisition' }));
  fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
  await screen.findByRole('link', { name: 'Back to piece' });
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  expect(window.location.pathname).toBe('/acquisitions/fair/from/stone');
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  expect(screen.getByRole('searchbox')).toHaveValue('Unsent collection search');
  expect(screen.getByRole('button', { name: 'List', pressed: true })).toBeVisible();
  expect(screen.getByRole('button', { name: 'Load more' })).toBeVisible();
  await waitFor(() => expect(screen.getByRole('link', { name: /Blue sapphire/ })).toHaveFocus());
  expect(scroll).toHaveBeenCalled();
  window.history.replaceState(null, '', '/');
});
