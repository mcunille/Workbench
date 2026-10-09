import { http, HttpResponse } from 'msw';
import { server } from '../test/server';

// A fresh module gives each simulated browser session its own token cache.
async function session() {
  vi.resetModules();
  return import('./serviceAdmin');
}

describe('service-admin authentication transport', () => {
  it('admin_login_refreshes_csrf', async () => {
    // GIVEN distinct anonymous and authenticated service-admin antiforgery tokens.
    let signedIn = false;
    let loginToken: string | null = null;
    server.use(
      http.get('*/api/beta/service-admin/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: signedIn ? 'admin-signed-in' : 'admin-anonymous' })),
      http.post('*/api/beta/service-admin/auth/login', ({ request }) => {
        loginToken = request.headers.get('X-CSRF-TOKEN');
        signedIn = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const admin = await session();
    // WHEN the service admin signs in and prepares a subsequent mutation.
    await admin.signInServiceAdmin('curator@example.test', 'synthetic-password');
    const headers = await admin.serviceAdminMutationHeaders();
    // THEN login used admin antiforgery and the authenticated mutation uses a fresh token.
    expect(loginToken).toBe('admin-anonymous');
    expect(headers['X-CSRF-TOKEN']).toBe('admin-signed-in');
  });

  it('reads only the service-admin identity and treats an expired session as signed out', async () => {
    // GIVEN an authenticated service-admin session which expires on the next identity check.
    let expired = false;
    server.use(http.get('*/api/beta/service-admin/auth/me', () => expired
      ? new HttpResponse(null, { status: 401 })
      : HttpResponse.json({ accountId: 'admin-id', email: 'curator@example.test' })));
    const admin = await session();
    // WHEN identity is read before and after expiry.
    const identity = await admin.getServiceAdminIdentity();
    expired = true;
    const signedOut = await admin.getServiceAdminIdentity();
    // THEN the admin identity is returned initially and expiry never invents an identity.
    expect(identity).toEqual({ accountId: 'admin-id', email: 'curator@example.test' });
    expect(signedOut).toBeNull();
  });

  it('signs out with admin antiforgery and drops the authenticated token', async () => {
    // GIVEN an authenticated admin token and a successful admin logout endpoint.
    let signedOut = false;
    let logoutToken: string | null = null;
    server.use(
      http.get('*/api/beta/service-admin/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: signedOut ? 'anonymous' : 'authenticated' })),
      http.post('*/api/beta/service-admin/auth/logout', ({ request }) => {
        logoutToken = request.headers.get('X-CSRF-TOKEN');
        signedOut = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const admin = await session();
    // WHEN the service admin signs out.
    await admin.signOutServiceAdmin();
    const headers = await admin.serviceAdminMutationHeaders();
    // THEN logout used the admin session and later writes cannot reuse its token.
    expect(logoutToken).toBe('authenticated');
    expect(headers['X-CSRF-TOKEN']).toBe('anonymous');
  });

  it('rejects unavailable identity and login failures instead of claiming success', async () => {
    // GIVEN an unavailable admin identity endpoint and rejected credentials.
    server.use(
      http.get('*/api/beta/service-admin/auth/me', () => new HttpResponse(null, { status: 503 })),
      http.get('*/api/beta/service-admin/auth/antiforgery', () => HttpResponse.json({ requestToken: 'anonymous' })),
      http.post('*/api/beta/service-admin/auth/login', () => new HttpResponse(null, { status: 401 })),
    );
    const admin = await session();
    // WHEN either operation is attempted THEN its status reaches the caller for recovery.
    await expect(admin.getServiceAdminIdentity()).rejects.toMatchObject({ status: 503 });
    await expect(admin.signInServiceAdmin('curator@example.test', 'incorrect')).rejects.toMatchObject({ status: 401 });
  });

  it('allows another antiforgery request after the first request fails', async () => {
    // GIVEN a transient failure retrieving an admin token.
    let available = false;
    server.use(http.get('*/api/beta/service-admin/auth/antiforgery', () => available
      ? HttpResponse.json({ requestToken: 'recovered' })
      : new HttpResponse(null, { status: 503 })));
    const admin = await session();
    // WHEN the failed operation is retried after recovery.
    await expect(admin.serviceAdminMutationHeaders()).rejects.toMatchObject({ status: 503 });
    available = true;
    const headers = await admin.serviceAdminMutationHeaders();
    // THEN the caller receives a usable token rather than a permanently cached rejection.
    expect(headers['X-CSRF-TOKEN']).toBe('recovered');
  });
});
