import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { AuthProvider } from './AuthContext';
import { SignIn } from './SignIn';

describe('SignIn', () => {
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
