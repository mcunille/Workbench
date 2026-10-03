import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from '../../App';
import { ServiceAdminApplication } from './ServiceAdminApplication';
import * as admin from '../../api/serviceAdmin';
import * as tenant from '../../api/auth';

vi.mock('../../api/serviceAdmin', () => ({
  getServiceAdminIdentity: vi.fn(), signInServiceAdmin: vi.fn(), signOutServiceAdmin: vi.fn(),
}));
vi.mock('../../api/auth', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../api/auth')>(), getCurrentIdentity: vi.fn(),
}));
vi.mock('../../api/system', () => ({ getSystem: vi.fn().mockResolvedValue({ name: 'Workbench', version: '1.0.0' }) }));

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue(null);
  vi.mocked(tenant.getCurrentIdentity).mockResolvedValue({ userId: 'tenant', email: 'tenant@example.test', tenantName: 'Tenant', permissions: ['TenantAccess'] });
  window.history.replaceState(null, '', '/service-admin/gem-reference');
});
afterEach(() => window.history.replaceState(null, '', '/'));

describe('service-admin entry', () => {
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
