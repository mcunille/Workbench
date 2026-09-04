import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from '../../App';
import { server } from '../../test/server';

describe('authentication bootstrap', () => {
  it('never mounts protected content before durable identity succeeds', async () => {
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
      http.get('*/api/auth/me', () =>
        new HttpResponse(null, { status: 401 }),
      ),
    );

    render(<App />);

    expect(screen.queryByText('Tenant users')).not.toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
  });
});
