import { fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { TenantUsers } from './TenantUsers';

describe('TenantUsers', () => {
  it('lists users and sends bounded invitation commands', async () => {
    let invitation: unknown;
    let revokedUserId: string | undefined;
    server.use(
      http.get('*/api/beta/tenant/users', () =>
        HttpResponse.json([
          {
            id: '11111111-1111-1111-1111-111111111111',
            email: 'admin@example.com',
            state: 1,
          },
        ]),
      ),
      http.get('*/api/beta/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'request-token' }),
      ),
      http.post('*/api/beta/tenant/users/invitations', async ({ request }) => {
        invitation = await request.json();
        return new HttpResponse(null, { status: 202 });
      }),
      http.delete('*/api/beta/tenant/users/:userId/sessions', ({ params }) => {
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

it.each([403, 404])('keeps the role editor mounted until pending recovery ends with status %s', async (status) => {
  // GIVEN two enabled users and a role command with an unresolved response
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  let attempts = 0;
  const lost = vi.fn();
  server.use(
    http.get('*/api/beta/tenant/users', () => HttpResponse.json([
      { id: 'first', email: 'first@example.com', state: 1 },
      { id: 'second', email: 'second@example.com', state: 1 },
    ])),
    http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: 'reader', name: 'Accounting reader', permissions: ['AccountingReportsRead'] }])),
    http.get('*/api/beta/tenant/users/:userId/accounting-roles', ({ params }) => HttpResponse.json({ userId: params.userId, roleIds: [], version: 'v1' })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/tenant/users/:userId/accounting-roles', async () => {
      if (++attempts === 1) { await pending; return HttpResponse.error(); }
      return new HttpResponse(null, { status });
    }),
  );
  render(<TenantUsers onAuthLost={lost} />);
  fireEvent.click((await screen.findAllByRole('button', { name: 'Accounting roles' }))[0]);
  fireEvent.click(await screen.findByLabelText('Accounting reader'));
  // WHEN saving THEN switching the target cannot unmount the pending editor
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  const switches = screen.getAllByRole('button', { name: 'Accounting roles' });
  expect(switches[1]).toBeDisabled();
  fireEvent.click(switches[1]);
  expect(screen.getByRole('heading', { name: 'Accounting roles for first@example.com' })).toBeVisible();
  release();
  await screen.findByText('Roles could not be saved. Retry the same assignment.');
  expect(switches[1]).toBeDisabled();
  // AND denied access on retry clears private selections and releases the pending guard
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await screen.findByText(status === 403 ? 'Access denied. Private role selections have been cleared.' : 'This user is unavailable. Close this editor and choose an enabled user.');
  expect(screen.queryByLabelText('Accounting reader')).not.toBeInTheDocument();
  expect(switches[1]).toBeEnabled();
  if (status === 403) expect(lost).toHaveBeenCalled(); else expect(lost).not.toHaveBeenCalled();
});

