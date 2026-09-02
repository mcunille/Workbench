import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { TenantUsers } from './TenantUsers';

describe('TenantUsers', () => {
  it('lists users and sends bounded invitation commands', async () => {
    let invitation: unknown;
    let revokedUserId: string | undefined;
    server.use(
      http.get('*/api/tenant/users', () =>
        HttpResponse.json([
          {
            id: '11111111-1111-1111-1111-111111111111',
            email: 'admin@example.com',
            state: 1,
          },
        ]),
      ),
      http.get('*/api/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'request-token' }),
      ),
      http.post('*/api/tenant/users/invitations', async ({ request }) => {
        invitation = await request.json();
        return new HttpResponse(null, { status: 202 });
      }),
      http.delete('*/api/tenant/users/:userId/sessions', ({ params }) => {
        revokedUserId = String(params.userId);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    render(<TenantUsers />);

    expect(await screen.findByText('admin@example.com')).toBeVisible();
    fireEvent.change(screen.getByLabelText('Invite email'), {
      target: { value: 'new@example.com' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Send invitation' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Invitation queued');
    expect(invitation).toEqual({ email: 'new@example.com' });

    fireEvent.click(screen.getByRole('button', { name: 'Revoke sessions' }));
    expect(await screen.findByRole('status')).toHaveTextContent('User sessions revoked');
    expect(revokedUserId).toBe('11111111-1111-1111-1111-111111111111');
  });
});
