import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
      http.get('*/api/beta/auth/me', async () => {
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
      http.get('*/api/beta/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
      http.get('*/api/beta/auth/me', () => new HttpResponse(null, { status: 401 })),
    );

    render(<App />);

    expect(screen.queryByText('Tenant users')).not.toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { name: 'Sign in' }),
    ).toBeVisible();
  });
});

function AuthRaceProbe({ refreshed }: { refreshed(): void }) {
  const { identity, status, refresh, signIn, signOut } = useAuth();
  return <>
    <p role="status">{status}</p>
    {identity ? <p>{identity.tenantName}</p> : null}
    <button onClick={() => void refresh('permissions').finally(refreshed)}>Refresh permissions</button>
    <button onClick={() => void signOut()}>Sign out probe</button>
    <button onClick={() => void signIn('new@example.com', 'test-password')}>Sign in new user</button>
  </>;
}

it('ignores an old permission response after a later sign-out completes', async () => {
  // GIVEN a permission refresh holding a response for the previously signed-in user
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let reads = 0;
  const refreshed = vi.fn();
  server.use(
    http.get('*/api/beta/auth/me', async () => {
      if (++reads === 2) await held;
      return HttpResponse.json({ userId: 'old', tenantName: 'Old tenant', email: 'old@example.com', permissions: [] });
    }),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/auth/logout', () => new HttpResponse(null, { status: 204 })),
  );
  render(<AuthProvider><AuthRaceProbe refreshed={refreshed} /></AuthProvider>);
  await screen.findByText('Old tenant');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  await waitFor(() => expect(reads).toBe(2));
  // WHEN sign-out finishes before that old response arrives
  fireEvent.click(screen.getByRole('button', { name: 'Sign out probe' }));
  await screen.findByText('signed-out');
  await act(async () => release());
  await waitFor(() => expect(refreshed).toHaveBeenCalled());
  // THEN the old response cannot restore authenticated UI state
  expect(screen.getByRole('status')).toHaveTextContent('signed-out');
  expect(screen.queryByText('Old tenant')).not.toBeInTheDocument();
});

it.each([200, 500])('ignores a stale permission response (%s) after a newer user signs in', async (responseStatus) => {
  // GIVEN an outstanding permission check from the earlier identity
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let reads = 0;
  const refreshed = vi.fn();
  server.use(
    http.get('*/api/beta/auth/me', async () => {
      const read = ++reads;
      if (read === 2) {
        await held;
        if (responseStatus === 500) return new HttpResponse(null, { status: 500 });
      }
      return HttpResponse.json({ userId: read > 2 ? 'new' : 'old', tenantName: read > 2 ? 'New tenant' : 'Old tenant', email: null, permissions: [] });
    }),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/auth/login', () => new HttpResponse(null, { status: 204 })),
  );
  render(<AuthProvider><AuthRaceProbe refreshed={refreshed} /></AuthProvider>);
  await screen.findByText('Old tenant');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  await waitFor(() => expect(reads).toBe(2));
  // WHEN a new sign-in completes before the stale result or error
  fireEvent.click(screen.getByRole('button', { name: 'Sign in new user' }));
  await screen.findByText('New tenant');
  await act(async () => release());
  await waitFor(() => expect(refreshed).toHaveBeenCalled());
  // THEN both stale success and stale failure leave the newer identity intact
  expect(screen.getByRole('status')).toHaveTextContent('signed-in');
  expect(screen.getByText('New tenant')).toBeVisible();
  expect(screen.queryByText('Old tenant')).not.toBeInTheDocument();
});


it('does not let a permission refresh supersede an in-flight sign-out', async () => {
  // GIVEN logout is in progress while the old session can still answer identity reads
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let reads = 0; let loggingOut = false;
  const refreshed = vi.fn();
  server.use(
    http.get('*/api/beta/auth/me', () => { reads++; return HttpResponse.json({ userId: 'old', tenantName: 'Old tenant', email: null, permissions: [] }); }),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/auth/logout', async () => { loggingOut = true; await held; return new HttpResponse(null, { status: 204 }); }),
  );
  render(<AuthProvider><AuthRaceProbe refreshed={refreshed} /></AuthProvider>);
  await screen.findByText('Old tenant');
  fireEvent.click(screen.getByRole('button', { name: 'Sign out probe' }));
  await waitFor(() => expect(loggingOut).toBe(true));
  // WHEN an earlier role save requests a permission refresh during logout
  fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }));
  await waitFor(() => expect(refreshed).toHaveBeenCalled());
  const readsDuringLogout = reads;
  await act(async () => release());
  // THEN no stale identity read supersedes logout and its completion signs out
  expect(readsDuringLogout).toBe(1);
  await screen.findByText('signed-out');
  expect(screen.queryByText('Old tenant')).not.toBeInTheDocument();
});
