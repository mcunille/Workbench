import { fireEvent, render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

describe('App', () => {
  it('retains search through details and discards it when authentication is lost', async () => {
    // GIVEN an authenticated collector with a submitted search.
    window.history.replaceState(null, '', '/inventory');
    let signedIn = true;
    let reads = 0;
    let deny = false;
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1' }),
      ),
      http.get('*/api/auth/me', () =>
        signedIn
          ? HttpResponse.json({
              userId: 'user',
              tenantName: 'Studio',
              email: 'person@example.test',
              permissions: ['TenantAccess'],
            })
          : new HttpResponse(null, { status: 401 }),
      ),
      http.get('*/api/items', () => {
        reads++;
        return HttpResponse.json({
          items: [
            {
              id: 'stone',
              name: 'Stone',
              location: null,
              photo: null,
              createdAtUtc: '2026-09-06T00:00:00Z',
            },
          ],
          nextCursor: null,
        });
      }),
      http.get('*/api/items/stone', () =>
        deny
          ? new HttpResponse(null, { status: 401 })
          : HttpResponse.json({
              id: 'stone',
              name: 'Stone',
              notes: null,
              location: null,
              photo: null,
              version: 'v',
              createdAtUtc: '2026-09-06T00:00:00Z',
            }),
      ),
    );
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    render(<App />);
    await screen.findByRole('link', { name: /Stone/ });
    fireEvent.change(screen.getByRole('searchbox'), {
      target: { value: 'private draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await screen.findByRole('link', { name: /Stone/ });
    // WHEN visiting details and returning THEN the traversal stays in authenticated memory.
    fireEvent.click(screen.getByRole('link', { name: /Stone/ }));
    await screen.findByRole('heading', { name: 'Stone' });
    fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
    expect(screen.getByRole('searchbox')).toHaveValue('private draft');
    expect(reads).toBe(2);
    expect(window.location.search).toBe('');
    expect(JSON.stringify(window.history.state)).not.toContain('private');
    // WHEN the next protected request loses authentication THEN the collection is gone.
    deny = true;
    signedIn = false;
    fireEvent.click(screen.getByRole('link', { name: /Stone/ }));
    await screen.findByRole('heading', { name: 'Sign in' });
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    // WHEN the same person signs in again THEN the private traversal starts fresh.
    server.use(
      http.get('*/api/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'test' }),
      ),
      http.post('*/api/auth/login', () => {
        signedIn = true;
        deny = false;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    fireEvent.change(screen.getByLabelText('Email'), {
      target: { value: 'person@example.test' },
    });
    fireEvent.change(screen.getByLabelText('Password'), {
      target: { value: 'test-password' },
    });
    fireEvent.submit(
      screen.getByRole('button', { name: 'Sign in' }).closest('form')!,
    );
    await screen.findByRole('heading', { name: 'Stone' });
    fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
    await screen.findByRole('link', { name: /Stone/ });
    expect(screen.getByRole('searchbox')).toHaveValue('');
    expect(reads).toBe(3);
    window.history.replaceState(null, '', '/');
  });
  it('retains an explicit appearance through sign-in and sign-out when storage is denied', async () => {
    // GIVEN unavailable preference storage and a signed-out user.
    const get = vi
      .spyOn(Storage.prototype, 'getItem')
      .mockImplementation(() => {
        throw new Error('denied');
      });
    const set = vi
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(() => {
        throw new Error('denied');
      });
    let signedIn = false;
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
      http.get('*/api/auth/me', () =>
        signedIn
          ? HttpResponse.json({
              userId: 'user',
              email: 'collector@example.test',
              tenantName: 'Studio',
              permissions: ['TenantAccess'],
            })
          : new HttpResponse(null, { status: 401 }),
      ),
      http.get('*/api/auth/antiforgery', () =>
        HttpResponse.json({ requestToken: 'test' }),
      ),
      http.post('*/api/auth/login', () => {
        signedIn = true;
        return new HttpResponse(null, { status: 204 });
      }),
      http.post('*/api/auth/logout', () => {
        signedIn = false;
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('*/api/items', () =>
        HttpResponse.json({ items: [], nextCursor: null }),
      ),
    );
    try {
      render(<App />);
      await screen.findByRole('heading', { name: 'Sign in' });
      // WHEN an explicit choice is made before authenticating.
      fireEvent.change(screen.getByRole('combobox', { name: 'Appearance' }), {
        target: { value: 'dark' },
      });
      fireEvent.change(screen.getByLabelText('Email'), {
        target: { value: 'collector@example.test' },
      });
      fireEvent.change(screen.getByLabelText('Password'), {
        target: { value: 'test-password' },
      });
      fireEvent.submit(
        screen.getByRole('button', { name: 'Sign in' }).closest('form')!,
      );
      await screen.findByRole('heading', { name: 'Collection' });
      // THEN the choice survives both authentication boundary changes in this page.
      expect(screen.getByRole('combobox', { name: 'Appearance' })).toHaveValue(
        'dark',
      );
      expect(document.documentElement.dataset.theme).toBe('dark');
      fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
      await screen.findByRole('heading', { name: 'Sign in' });
      expect(screen.getByRole('combobox', { name: 'Appearance' })).toHaveValue(
        'dark',
      );
    } finally {
      get.mockRestore();
      set.mockRestore();
    }
  });
  it('keeps workspace controls together and release metadata compact', async () => {
    // GIVEN an authenticated workspace with a full build identifier.
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({
          name: 'Workbench',
          version: '1.2.3+abcdef0123456789',
        }),
      ),
      http.get('*/api/auth/me', () =>
        HttpResponse.json({
          userId: '11111111-1111-1111-1111-111111111111',
          email: 'admin@example.com',
          tenantName: 'Tenant A',
          permissions: ['TenantAccess'],
        }),
      ),
      http.get('*/api/items', () =>
        HttpResponse.json({ items: [], nextCursor: null }),
      ),
    );

    // WHEN the collection shell loads.
    render(<App />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading');
    expect(
      await screen.findByRole('heading', { name: 'Collection' }),
    ).toBeVisible();
    // THEN appearance and sign-out remain together in the workspace header.
    const header = within(screen.getByRole('banner'));
    expect(header.getByRole('combobox', { name: 'Appearance' })).toBeVisible();
    expect(header.getByRole('button', { name: 'Sign out' })).toBeVisible();
    expect(
      screen.getAllByRole('combobox', { name: 'Appearance' }),
    ).toHaveLength(1);
    // AND the release label stays compact while retaining the full build as metadata.
    expect(screen.getByText('Workbench 1.2.3')).toHaveAttribute(
      'title',
      '1.2.3+abcdef0123456789',
    );
  });

  it('renders a safe failure state', async () => {
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json(
          {
            type: 'https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1',
            title: 'An unexpected error occurred.',
            status: 500,
            traceId: 'test-trace',
          },
          { status: 500 },
        ),
      ),
      http.get('*/api/auth/me', () => new HttpResponse(null, { status: 401 })),
    );

    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Workbench is temporarily unavailable.',
    );
    expect(
      screen.queryByText('An unexpected error occurred.'),
    ).not.toBeInTheDocument();
  });
});
