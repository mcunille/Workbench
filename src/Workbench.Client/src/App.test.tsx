import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

describe('App', () => {
  it('renders the typed application identity', async () => {
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
    );

    render(<App />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading');
    expect(await screen.findByRole('heading', { name: 'Workbench' })).toBeVisible();
    expect(screen.getByText('Version 1.2.3')).toBeVisible();
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
    );

    render(<App />);

    expect(
      await screen.findByRole('alert'),
    ).toHaveTextContent('Workbench is temporarily unavailable.');
    expect(screen.queryByText('An unexpected error occurred.')).not.toBeInTheDocument();
  });
});
