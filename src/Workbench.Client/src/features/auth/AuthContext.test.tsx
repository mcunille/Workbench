import { act, fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from '../../App';
import { server } from '../../test/server';
import { AuthProvider } from './AuthContext';
import { useAuth } from './useAuth';

function AuthProbe() {
  const { identity, status, refresh } = useAuth();
  return (
    <>
      <p role="status">{status}</p>
      {identity ? <p>{identity.tenantName}</p> : null}
      <button onClick={() => void refresh()}>Refresh identity</button>
    </>
  );
}

describe('authentication bootstrap', () => {
  it('hides the prior identity immediately while rechecking authentication', async () => {
    // GIVEN an authenticated identity and an access recheck that has not returned.
    let complete!: () => void;
    const pending = new Promise<void>((resolve) => {
      complete = resolve;
    });
    let calls = 0;
    server.use(
      http.get('*/api/auth/me', async () => {
        if (++calls > 1) {
          await pending;
          return new HttpResponse(null, { status: 401 });
        }
        return HttpResponse.json({
          userId: 'user',
          tenantName: 'Private tenant',
          email: null,
          permissions: [],
        });
      }),
    );
    render(
      <AuthProvider>
        <AuthProbe />
      </AuthProvider>,
    );
    await screen.findByText('Private tenant');
    // WHEN access is rechecked THEN protected identity is unavailable before the response.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh identity' }));
    expect(screen.queryByText('Private tenant')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('loading');
    await act(async () => {
      complete();
    });
    expect(await screen.findByText('signed-out')).toBeVisible();
  });
  it('never mounts protected content before durable identity succeeds', async () => {
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
      http.get('*/api/auth/me', () => new HttpResponse(null, { status: 401 })),
    );

    render(<App />);

    expect(screen.queryByText('Tenant users')).not.toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeVisible();
  });
});
