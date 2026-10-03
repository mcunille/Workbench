import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from '../../App';
import { ServiceAdminApplication } from './ServiceAdminApplication';
import * as admin from '../../api/serviceAdmin';
import * as tenant from '../../api/auth';
import * as gems from '../../api/gemReferenceAdmin';

vi.mock('../../api/serviceAdmin', () => ({
  getServiceAdminIdentity: vi.fn(), signInServiceAdmin: vi.fn(), signOutServiceAdmin: vi.fn(),
}));
vi.mock('../../api/auth', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../api/auth')>(), getCurrentIdentity: vi.fn(),
}));
vi.mock('../../api/system', () => ({ getSystem: vi.fn().mockResolvedValue({ name: 'Workbench', version: '1.0.0' }) }));
vi.mock('../../api/gemReferenceAdmin', async (original) => ({
  ...await original<typeof import('../../api/gemReferenceAdmin')>(),
  browseSharedGems: vi.fn().mockResolvedValue({ entries: [], nextCursor: null }),
  listGemDrafts: vi.fn().mockResolvedValue({ drafts: [], nextCursor: null }),
  getGemDraft: vi.fn(), getSharedGem: vi.fn(), saveGemDraft: vi.fn(),
}));

const savedDraft: gems.GemReferenceDraftResponse = { id: 'draft', entryId: 'entry', rowVersion: 'draft-v1', expectedPublishedRowVersion: null, content: { id: 'entry', materialKind: 'organic', commonName: 'Amber', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null }, createdBy: 'admin', updatedBy: 'admin', createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', errors: {} };

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue(null);
  vi.mocked(tenant.getCurrentIdentity).mockResolvedValue({ userId: 'tenant', email: 'tenant@example.test', tenantName: 'Tenant', permissions: ['TenantAccess'] });
  vi.mocked(gems.getGemDraft).mockResolvedValue(savedDraft);
  vi.mocked(gems.getSharedGem).mockRejectedValue(new gems.GemReferenceAdminApiError(404, {}));
  window.history.replaceState(null, '', '/service-admin/gem-reference');
});
afterEach(() => window.history.replaceState(null, '', '/'));

describe('service-admin entry', () => {
  it.each(['admin', 'other-admin'])('keeps expired-session edits only for the original account (%s)', async (accountId) => {
    // GIVEN an authenticated editor with local changes and a revoked session.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce({ accountId: 'admin', email: 'curator@example.test' })
      .mockResolvedValue({ accountId, email: 'curator@example.test' });
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    vi.mocked(gems.saveGemDraft).mockRejectedValue(new gems.GemReferenceAdminApiError(401, {}));
    window.history.replaceState(null, '', '/service-admin/gem-reference/drafts/draft');
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Unsaved amber' } });
    // WHEN saving discovers expiry THEN reauthentication appears while the editor remains mounted.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved amber');
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
    // WHEN an admin signs in THEN the same account refreshes versions; a different account starts without those edits.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    if (accountId === 'admin') {
      await waitFor(() => expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled());
      expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved amber');
      expect(gems.getGemDraft).toHaveBeenCalledTimes(2);
      expect(gems.getSharedGem).toHaveBeenCalledWith('entry');
    } else {
      await screen.findByRole('heading', { name: 'Gem reference' });
      expect(screen.queryByLabelText('Common name')).not.toBeInTheDocument();
      expect(window.location.pathname).toBe('/service-admin/gem-reference');
    }
  });

  it('uses the existing discard decision before leaving a dirty editor or signing out', async () => {
    // GIVEN local edits in an authenticated new draft.
    const originalModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(admin.signOutServiceAdmin).mockResolvedValue();
    window.history.replaceState(null, '', '/service-admin/gem-reference/new');
    render(<ServiceAdminApplication appearance={null} />);
    fireEvent.change(await screen.findByLabelText('Common name'), { target: { value: 'Unsaved' } });
    // WHEN library navigation is requested THEN keeping edits preserves the form and route.
    fireEvent.click(screen.getByRole('link', { name: 'Gem reference' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('Discard changes?');
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved');
    expect(window.location.pathname).toBe('/service-admin/gem-reference/new');
    // WHEN signing out is requested THEN only explicit discard performs logout.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByRole('dialog');
    expect(admin.signOutServiceAdmin).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    expect(admin.signOutServiceAdmin).toHaveBeenCalledOnce();
    if (originalModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalModal);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
  });
  it('tenant_cookie_does_not_authenticate_admin', async () => {
    // GIVEN a valid tenant session and an absent service-admin session.
    // WHEN the browser opens a direct service-admin route.
    render(<App />);
    // THEN dedicated admin sign-in renders without ever consulting tenant identity.
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
    expect(tenant.getCurrentIdentity).not.toHaveBeenCalled();
    expect(admin.getServiceAdminIdentity).toHaveBeenCalled();
  });

  it('both_cookies_use_admin_identity', async () => {
    // GIVEN both identities are available in the browser.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    // WHEN a direct service-admin route is opened.
    render(<App />);
    // THEN only the independent admin identity and shared-work navigation appear.
    expect(await screen.findByText('curator@example.test')).toBeVisible();
    expect(tenant.getCurrentIdentity).not.toHaveBeenCalled();
    expect(screen.queryByText('tenant@example.test')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Inventory' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Gem reference' })).toHaveAttribute('href', '/service-admin/gem-reference');
  });

  it('failed_login_keeps_email', async () => {
    // GIVEN a signed-out admin and a rejected sign-in attempt.
    vi.mocked(admin.signInServiceAdmin).mockRejectedValue(new Error('Rejected'));
    render(<ServiceAdminApplication appearance={<span>Appearance</span>} />);
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    // WHEN the admin enters credentials and submits.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'incorrect' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    // THEN a visible error leaves the entered email ready to retry.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not sign you in');
    expect(screen.getByLabelText('Email')).toHaveValue('curator@example.test');
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled();
  });

  it('rechecks admin identity after sign-in and preserves the requested URL', async () => {
    // GIVEN a deep admin URL requiring authentication.
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce(null)
      .mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    window.history.replaceState(null, '', '/service-admin/gem-reference/drafts/draft-id');
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    // WHEN credentials are accepted.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    // THEN verified admin identity unlocks the shell at the requested URL.
    expect(await screen.findByText('curator@example.test')).toBeVisible();
    expect(admin.signInServiceAdmin).toHaveBeenCalledWith('curator@example.test', 'synthetic-password');
    expect(window.location.pathname).toBe('/service-admin/gem-reference/drafts/draft-id');
  });

  it('offers retry when admin identity is unavailable', async () => {
    // GIVEN an unavailable identity service which recovers.
    vi.mocked(admin.getServiceAdminIdentity).mockRejectedValueOnce(new Error('Unavailable'));
    render(<ServiceAdminApplication appearance={null} />);
    // WHEN the initial identity check fails and the admin retries.
    expect(await screen.findByRole('alert')).toHaveTextContent('temporarily unavailable');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    // THEN the dedicated sign-in form appears after the successful check.
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
  });

  it('keeps the authenticated shell on failed logout and signs out after a successful retry', async () => {
    // GIVEN an admin session and a transient logout failure.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(admin.signOutServiceAdmin).mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValue();
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByText('curator@example.test');
    // WHEN logout fails.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    // THEN identity remains visible and the error allows another attempt.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not sign you out');
    expect(screen.getByText('curator@example.test')).toBeVisible();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled());
    // WHEN logout succeeds THEN admin sign-in replaces the shared shell.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
    expect(window.location.pathname).toBe('/service-admin/sign-in');
  });
});
