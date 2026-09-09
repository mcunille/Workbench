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
    // THEN the application navigation exposes the complete product name as one wordmark.
    expect(screen.getByRole('img', { name: 'Workbench' })).toBeVisible();
    // AND the collection attribution is reserved for sign-in.
    expect(screen.queryByText(/The White Stag Collection/)).not.toBeInTheDocument();
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
      fireEvent.click(screen.getByRole('switch', { name: 'Dark theme' }));
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
      fireEvent.click(screen.getByRole('button', { name: 'User menu' }));
      expect(screen.getByRole('button', { name: 'Appearance Dark' })).toBeVisible();
      expect(document.documentElement.dataset.theme).toBe('dark');
      fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
      await screen.findByRole('heading', { name: 'Sign in' });
      expect(screen.getByRole('switch', { name: 'Dark theme' })).toHaveAttribute('aria-checked', 'true');
    } finally {
      get.mockRestore();
      set.mockRestore();
    }
  });
  it.each([false, true])('keeps workspace controls in the nav for administrator=%s', async (administrator) => {
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
          permissions: administrator ? ['TenantAccess', 'TenantUsersManage'] : ['TenantAccess'],
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
    // THEN administration is a direct destination and personal controls stay behind the user row.
    expect(screen.queryByRole('banner')).not.toBeInTheDocument();
    expect(screen.queryByRole('switch', { name: 'Dark theme' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Administration' }) !== null).toBe(administrator);
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Account' })).not.toBeInTheDocument();
    const nav = within(screen.getByRole('navigation', { name: 'Workspace' }));
    expect(nav.getByRole('img', { name: 'Workbench' })).toBeVisible();
    expect(nav.getByText('Tenant A')).not.toBeVisible();
    const trigger = nav.getByRole('button', { name: 'User menu' });
    expect(trigger).toHaveAccessibleDescription('admin@example.com');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    // WHEN collapsing the sidebar THEN destinations retain accessible names and the account panel works.
    fireEvent.click(nav.getByRole('button', { name: 'Collapse navigation' }));
    expect(nav.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false');
    expect(nav.getByRole('link', { name: 'Inventory' })).toHaveAttribute('href', '/inventory');
    // WHEN the user row is expanded THEN both personal actions are available in the nav.
    fireEvent.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    // THEN the tenant appears only inside the menu and appearance is a direct cycling action.
    expect(nav.getByText('Tenant A')).toBeVisible();
    expect(nav.queryByRole('switch')).not.toBeInTheDocument();
    expect(nav.getByRole('link', { name: 'Account' })).toHaveAttribute('href', '/account');
    expect(nav.getByRole('button', { name: 'Sign out' })).toBeVisible();
    expect(nav.getByRole('button', { name: /Appearance/ })).toBeVisible();
    expect(nav.queryByRole('link', { name: 'Administration' }) !== null).toBe(administrator);
    // WHEN Escape is pressed THEN the disclosure closes and focus returns to its trigger.
    fireEvent.keyDown(nav.getByRole('button', { name: 'Sign out' }), { key: 'Escape' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(trigger).toHaveFocus();
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
    expect(
      screen.getAllByRole('button', { name: /Appearance/, hidden: true }),
    ).toHaveLength(1);
    // AND the release label stays compact while retaining the full build as metadata.
    expect(nav.getByText('Workbench 1.2.3')).toHaveAttribute(
      'title',
      '1.2.3+abcdef0123456789',
    );
    // WHEN expanding again THEN the labeled navigation returns without changing the page.
    fireEvent.click(nav.getByRole('button', { name: 'Expand navigation' }));
    expect(nav.getByRole('button', { name: 'Collapse navigation' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('heading', { name: 'Collection' })).toBeVisible();
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
