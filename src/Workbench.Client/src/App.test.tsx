import { fireEvent, render, screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

describe('App', () => {
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
