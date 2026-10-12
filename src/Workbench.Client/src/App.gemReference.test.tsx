import { fireEvent, render, screen } from '@testing-library/react';
import { App } from './App';
import * as auth from './api/auth';
import * as system from './api/system';
import * as gems from './api/gemReference';
import { referenceFixture } from './features/gemReference/gemFixture';

afterEach(() => { vi.restoreAllMocks(); window.history.replaceState(null, '', '/'); });
it('opens tenant gem browsing from the shell and recovers ended authority', async () => {
  // GIVEN a tenant member starts on the library with no special curation permission.
  window.history.replaceState(null, '', '/gem-reference');
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.spyOn(system, 'getSystem').mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.spyOn(auth, 'getCurrentIdentity').mockResolvedValueOnce({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] }).mockResolvedValue(null);
  vi.spyOn(gems, 'browseGems').mockResolvedValueOnce({ entries: [], nextCursor: null }).mockRejectedValueOnce(new gems.GemReferenceApiError(401, null));
  render(<App />);
  // WHEN the authenticated shell loads THEN library navigation is active and editing is absent.
  await screen.findByRole('heading', { name: 'Gem reference' });
  expect(screen.getByRole('link', { name: 'Gem reference' })).toHaveAttribute('aria-current', 'page');
  expect(screen.queryByRole('button', { name: /Add|Edit|Customize/ })).not.toBeInTheDocument();
  // WHEN a read loses authority THEN normal tenant sign-in replaces private content.
  await screen.findByText('No gem entries are available yet.');
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('button', { name: 'Sign in' });
  expect(screen.queryByRole('heading', { name: 'Gem reference' })).not.toBeInTheDocument();
});

it('dispatches origin-qualified details and returns to the private search', async () => {
  // GIVEN the shell has a filtered effective result.
  window.history.replaceState(null, '', '/gem-reference');
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.spyOn(system, 'getSystem').mockResolvedValue({ name: 'Workbench', version: '1' });
  vi.spyOn(auth, 'getCurrentIdentity').mockResolvedValue({ userId: 'person', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'] });
  vi.spyOn(gems, 'browseGems').mockResolvedValue({ entries: [{ ...referenceFixture, origin: 'tenant' }], nextCursor: null });
  vi.spyOn(gems, 'getGem').mockResolvedValue({ ...referenceFixture, origin: 'tenant' });
  render(<App />);
  fireEvent.click(await screen.findByRole('link', { name: 'Synthetic ruby' }));
  // WHEN opening its link THEN the shell requests the tenant origin explicitly.
  await screen.findByRole('heading', { name: 'Synthetic ruby' });
  expect(gems.getGem).toHaveBeenCalledWith('ruby', 'tenant', expect.any(AbortSignal));
  fireEvent.click(screen.getByRole('link', { name: 'Back to gem reference' }));
  // AND returning restores the selected result and active library destination.
  expect(await screen.findByRole('link', { name: 'Synthetic ruby' })).toHaveFocus();
});
