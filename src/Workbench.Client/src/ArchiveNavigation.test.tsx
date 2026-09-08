import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';
it('keeps archive and collection traversals independent through appearance and detail navigation, and clears both on identity loss', async () => {
  // GIVEN authenticated active and archived records with separate searches.
  window.history.replaceState(null, '', '/inventory');
  let signedIn = true;
  const item = {
    id: 'stone',
    name: 'Archived stone',
    location: null,
    notes: 'Keep',
    photo: null,
    version: 'v',
    archivedAtUtc: '2026-09-07T00:00:00Z',
    createdAtUtc: '2026-09-06T00:00:00Z',
  };
  server.use(
    http.get('*/api/system', () =>
      HttpResponse.json({ name: 'Workbench', version: '1' }),
    ),
    http.get('*/api/auth/me', () =>
      signedIn
        ? HttpResponse.json({
            userId: 'person',
            tenantName: 'Studio',
            email: 'person@example.test',
            permissions: ['TenantAccess'],
          })
        : new HttpResponse(null, { status: 401 }),
    ),
    http.get('*/api/items', () =>
      HttpResponse.json({
        items: [
          {
            ...item,
            id: 'active',
            name: 'Active stone',
            archivedAtUtc: null,
          },
        ],
        nextCursor: null,
      }),
    ),
    http.get('*/api/items/archived', () =>
      HttpResponse.json({ items: [item], nextCursor: null }),
    ),
    http.get('*/api/items/stone', () =>
      signedIn
        ? HttpResponse.json(item)
        : new HttpResponse(null, { status: 401 }),
    ),
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  render(<App />);
  await screen.findByRole('link', { name: /Active stone/ });
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'active draft' },
  });
  // WHEN opening the separate archive, searching and following a canonical detail link.
  fireEvent.click(screen.getByRole('link', { name: 'Archive' }));
  await screen.findByRole('heading', { name: 'Archive' });
  await screen.findByRole('link', { name: /Archived stone/ });
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'archive draft' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  fireEvent.change(screen.getByRole('combobox', { name: 'Appearance' }), {
    target: { value: 'dark' },
  });
  fireEvent.click(screen.getByRole('link', { name: /Archived stone/ }));
  await screen.findByRole('heading', { name: 'Archived stone' });
  expect(window.location.pathname).toBe('/inventory/stone');
  // THEN ordinary Back preserves the archive while switching back preserves the active draft.
  fireEvent.click(screen.getByRole('link', { name: 'Back to archive' }));
  expect(screen.getByRole('searchbox')).toHaveValue('archive draft');
  expect(
    screen.getByRole('button', { name: 'List', pressed: true }),
  ).toBeVisible();
  fireEvent.click(screen.getByRole('link', { name: 'Collection' }));
  expect(screen.getByRole('searchbox')).toHaveValue('active draft');
  fireEvent.click(screen.getByRole('link', { name: 'Archive' }));
  signedIn = false;
  fireEvent.click(screen.getByRole('link', { name: /Archived stone/ }));
  await screen.findByRole('heading', { name: 'Sign in' });
  server.use(
    http.get('*/api/auth/antiforgery', () =>
      HttpResponse.json({ requestToken: 'test' }),
    ),
    http.post('*/api/auth/login', () => {
      signedIn = true;
      return new HttpResponse(null, { status: 204 });
    }),
  );
  fireEvent.change(screen.getByLabelText('Email'), {
    target: { value: 'person@example.test' },
  });
  fireEvent.change(screen.getByLabelText('Password'), {
    target: { value: 'test-password' },
  });
  fireEvent.submit(
    screen.getByRole('button', { name: 'Sign in' }).closest('form')!,
  );
  await screen.findByRole('heading', { name: 'Archived stone' });
  fireEvent.click(screen.getByRole('link', { name: 'Back to archive' }));
  await screen.findByRole('link', { name: /Archived stone/ });
  expect(screen.getByRole('searchbox')).toHaveValue('');
  fireEvent.click(screen.getByRole('link', { name: 'Collection' }));
  await screen.findByRole('link', { name: /Active stone/ });
  expect(screen.getByRole('searchbox')).toHaveValue('');
  expect(JSON.stringify(window.history.state)).not.toContain('draft');
  window.history.replaceState(null, '', '/');
});
