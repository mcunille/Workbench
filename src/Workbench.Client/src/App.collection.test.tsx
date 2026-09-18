import { fireEvent, render, screen } from '@testing-library/react';
import { App } from './App';
import { ApiError, getCurrentIdentity, signIn } from './api/auth';
import { getItem, getItems } from './api/items';
import { getSystem } from './api/system';

vi.mock('./api/system', () => ({ getSystem: vi.fn() }));
vi.mock('./api/auth', async (original) => ({
  ...await original<typeof import('./api/auth')>(),
  getCurrentIdentity: vi.fn(),
  signIn: vi.fn(),
}));
vi.mock('./api/items', async (original) => ({
  ...await original<typeof import('./api/items')>(),
  getItems: vi.fn(),
  getItem: vi.fn(),
}));

afterEach(() => {
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
});

it('retains search through details and discards it when authentication is lost', async () => {
  // GIVEN an authenticated collector with a submitted search.
  // This component contract uses feature APIs; their HTTP behavior has separate tests.
  window.history.replaceState(null, '', '/inventory');
  let signedIn = true;
  let deny = false;
  const stone = {
    id: 'stone', name: 'Stone', location: null, photo: null,
    notes: null, version: 'v', createdAtUtc: '2026-09-06T00:00:00Z',
  };
  vi.mocked(getSystem).mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.mocked(getCurrentIdentity).mockImplementation(async () => signedIn ? {
    userId: 'user', tenantName: 'Studio', email: 'person@example.test',
    permissions: ['TenantAccess'],
  } : null);
  vi.mocked(getItems).mockResolvedValue({ items: [stone], nextCursor: null });
  vi.mocked(getItem).mockImplementation(async () => {
    if (deny) throw new ApiError(401);
    return stone;
  });
  vi.mocked(signIn).mockImplementation(async () => {
    signedIn = true;
    deny = false;
  });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  render(<App />);
  await screen.findByRole('link', { name: /Stone/ });
  // THEN navigation exposes one complete wordmark and reserves attribution for sign-in.
  expect(screen.getByRole('img', { name: 'Workbench' })).toBeVisible();
  expect(screen.queryByText(/The White Stag Collection/)).not.toBeInTheDocument();
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'private draft' } });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('link', { name: /Stone/ });
  // WHEN visiting details and returning THEN the traversal stays in authenticated memory.
  fireEvent.click(screen.getByRole('link', { name: /Stone/ }));
  await screen.findByRole('heading', { name: 'Stone' });
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  expect(screen.getByRole('searchbox')).toHaveValue('private draft');
  expect(getItems).toHaveBeenCalledTimes(2);
  expect(window.location.search).toBe('');
  expect(JSON.stringify(window.history.state)).not.toContain('private');
  // WHEN the next protected request loses authentication THEN the collection is gone.
  deny = true;
  signedIn = false;
  fireEvent.click(screen.getByRole('link', { name: /Stone/ }));
  await screen.findByRole('heading', { name: 'Sign in' });
  expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
  // WHEN the same person signs in again THEN the private traversal starts fresh.
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'person@example.test' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'test-password' } });
  fireEvent.submit(screen.getByRole('button', { name: 'Sign in' }).closest('form')!);
  await screen.findByRole('heading', { name: 'Stone' });
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  await screen.findByRole('link', { name: /Stone/ });
  expect(screen.getByRole('searchbox')).toHaveValue('');
  expect(getItems).toHaveBeenCalledTimes(3);
});
