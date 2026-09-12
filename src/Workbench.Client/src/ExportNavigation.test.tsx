import { fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';
import { prepareExport } from './api/export';

vi.mock('./api/export', () => ({ prepareExport: vi.fn() }));

it.each(['csv', 'zip'] as const)('retains %s format, scope and download across navigation and appearance, and clears private files at sign-out', async format => {
  // GIVEN an authenticated empty collection and a prepared CSV.
  window.history.replaceState(null, '', '/inventory');
  let signedIn = true;
  server.use(
    http.get('*/api/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/auth/me', () => signedIn ? HttpResponse.json({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] }) : new HttpResponse(null, { status: 401 })),
    http.get('*/api/items', () => HttpResponse.json({ items: [], nextCursor: null })),
    http.get('*/api/items/archived', () => HttpResponse.json({ items: [], nextCursor: null })),
    http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf' })),
    http.post('*/api/auth/logout', () => { signedIn = false; return new HttpResponse(null, { status: 204 }); }),
    http.post('*/api/auth/login', () => { signedIn = true; return new HttpResponse(null, { status: 204 }); }),
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  URL.createObjectURL = vi.fn(() => 'blob:export');
  URL.revokeObjectURL = vi.fn();
  vi.mocked(prepareExport).mockResolvedValue({ blob: new Blob(['complete']), filename: 'records.csv' });
  render(<App />);
  fireEvent.click(await screen.findByRole('link', { name: 'Export records' }));
  expect(window.location.pathname).toBe('/inventory/export');
  fireEvent.click(screen.getByRole('radio', { name: 'Active and archived records' }));
  if (format === 'zip') fireEvent.click(screen.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)' }));
  fireEvent.click(screen.getByRole('button', { name: 'Prepare export' }));
  await screen.findByRole('link', { name: `Download ${format.toUpperCase()}` });
  // WHEN navigating through collection and archive and changing appearance.
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  fireEvent.click(screen.getByRole('link', { name: 'Archive' }));
  fireEvent.click(screen.getByRole('link', { name: 'Export records' }));
  fireEvent.click(screen.getByRole('button', { name: 'User menu' }));
  const appearance = screen.getByRole('button', { name: /^Appearance / });
  const wasDark = appearance.getAttribute('aria-label') === 'Appearance Dark';
  fireEvent.click(appearance);
  expect(appearance).toHaveAttribute('aria-label', wasDark ? 'Appearance Light' : 'Appearance Dark');
  expect(document.documentElement).toHaveAttribute('data-theme', wasDark ? 'light' : 'dark');
  // THEN the same private file and explicit scope remain available.
  expect(screen.getByRole('radio', { name: 'Active and archived records' })).toBeChecked();
  expect(screen.getByRole('radio', { name: format === 'zip' ? 'Records, photographs and acquisition documents (ZIP)' : 'Records (CSV)' })).toBeChecked();
  expect(screen.getByRole('link', { name: `Download ${format.toUpperCase()}` })).toHaveAttribute('href', 'blob:export');
  expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
  // WHEN signing out and signing back in THEN the prepared file and prior scope are discarded.
  fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
  await screen.findByRole('heading', { name: 'Sign in' });
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:export');
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'person@example.test' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'test-password' } });
  fireEvent.submit(screen.getByRole('button', { name: 'Sign in' }).closest('form')!);
  await screen.findByRole('heading', { name: 'Export records' });
  expect(screen.getByRole('button', { name: 'Prepare export' })).toBeDisabled();
  expect(screen.queryByRole('link', { name: 'Download CSV' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Download ZIP' })).not.toBeInTheDocument();
  expect(screen.getByRole('radio', { name: 'Records (CSV)' })).toBeChecked();
  window.history.replaceState(null, '', '/');
});
