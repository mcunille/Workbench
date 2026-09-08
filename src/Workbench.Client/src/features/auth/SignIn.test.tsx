import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { AuthProvider } from './AuthContext';
import { SignIn } from './SignIn';
import { AuthContext } from './useAuth';

describe('SignIn', () => {
  it('identifies Workbench and its maker above the sign-in form', () => {
    // GIVEN a visitor who needs to sign in.
    const signedOut = {
      identity: null,
      status: 'signed-out' as const,
      refresh: vi.fn(),
      signIn: vi.fn(),
      signOut: vi.fn(),
    };

    // WHEN the sign-in page is presented.
    render(
      <AuthContext.Provider value={signedOut}>
        <SignIn />
      </AuthContext.Provider>,
    );

    // THEN the product and understated maker attribution identify the form.
    expect(screen.getByText('Workbench')).toBeVisible();
    expect(screen.getByText('by The White Stag Collection')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(screen.getByLabelText('Email')).toHaveAttribute(
      'autocomplete', 'username',
    );
    expect(screen.getByLabelText('Password')).toHaveAttribute(
      'autocomplete', 'current-password',
    );
    expect(screen.getByRole('link', { name: 'Forgot your password?' }))
      .toHaveAttribute('href', '/recover');
  });

  it('sends the antiforgery header without accepting tenant authority', async () => {
    let antiforgeryHeader: string | null = null;
    let identityRequests = 0;
    server.use(
      http.get('*/api/auth/me', () => {
        identityRequests++;
        return identityRequests === 1
          ? new HttpResponse(null, { status: 401 })
          : HttpResponse.json({
              userId: '11111111-1111-1111-1111-111111111111',
              email: 'admin@example.com',
              tenantName: 'Tenant A',
              permissions: ['TenantAccess'],
            });
      }),
      http.get('*/api/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'request-token' }),
      ),
      http.post('*/api/auth/login', ({ request }) => {
        antiforgeryHeader = request.headers.get('X-CSRF-TOKEN');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    render(
      <AuthProvider>
        <SignIn />
      </AuthProvider>,
    );

    expect(screen.queryByLabelText(/tenant/i)).not.toBeInTheDocument();
    fireEvent.change(await screen.findByLabelText('Email'), {
      target: { value: 'admin@example.com' },
    });
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'Correct Horse Battery Staple 1!' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('Signed in')).toBeVisible();
    expect(antiforgeryHeader).toBe('request-token');
  });
});
