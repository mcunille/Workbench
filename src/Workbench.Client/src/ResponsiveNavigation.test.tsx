import { act, fireEvent, render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

afterEach(() => vi.unstubAllGlobals());

it.each([false, true])('allows toggling and restores desktop choice when starting narrow=%s', async (initiallyNarrow) => {
  // GIVEN a signed-in collector starting on either side of the collapse breakpoint.
  const listeners = new Set<() => void>();
  const media = {
    matches: initiallyNarrow,
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
  };
  vi.stubGlobal('matchMedia', (query: string) =>
    query === '(width < 900px)' ? media : { ...media, matches: false },
  );
  server.use(
    http.get('*/api/system', () => HttpResponse.json({ name: 'Workbench', version: '1' })),
    http.get('*/api/auth/me', () => HttpResponse.json({
      userId: 'user', tenantName: 'Studio', email: 'person@example.test', permissions: ['TenantAccess'],
    })),
    http.get('*/api/items', () => HttpResponse.json({ items: [], nextCursor: null })),
  );
  const view = render(<App />);
  // AND the authenticated render, including its resize subscription, has settled.
  await screen.findByRole('heading', { name: 'Collection' });
  await act(async () => {});
  const resize = (narrow: boolean) => act(() => {
    media.matches = narrow;
    listeners.forEach(listener => listener());
  });
  if (initiallyNarrow) {
    // WHEN starting collapsed THEN the user can expand and collapse the sidebar.
    fireEvent.click(screen.getByRole('button', { name: 'Expand navigation' }));
    expect(screen.getByRole('navigation').parentElement).not.toHaveClass('navigation-collapsed');
    fireEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }));
    expect(screen.getByRole('navigation').parentElement).toHaveClass('navigation-collapsed');
    resize(false);
  }
  // WHEN the viewport narrows THEN navigation collapses automatically.
  resize(true);
  expect(screen.getByRole('navigation')).toHaveClass('workspace-nav');
  expect(screen.getByRole('navigation').parentElement).toHaveClass('navigation-collapsed');
  // WHEN widened THEN the expanded desktop choice returns.
  resize(false);
  expect(screen.getByRole('navigation').parentElement).not.toHaveClass('navigation-collapsed');
  // AND an explicitly collapsed desktop choice survives another resize cycle.
  fireEvent.click(screen.getByRole('button', { name: 'Collapse navigation' }));
  resize(true);
  resize(false);
  expect(screen.getByRole('navigation').parentElement).toHaveClass('navigation-collapsed');
  view.unmount();
  expect(listeners.size).toBe(0);
});
