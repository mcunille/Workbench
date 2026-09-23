import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';
it.each([['TenantAccess'], ['TenantAccess', 'AccountingReportsRead'], ['TenantAccess', 'TenantUsersManage']])('denies accounting setup deep links without configuration permission: %j', async (...permissions: string[]) => {
  // GIVEN an authenticated member without configuration access
  window.history.replaceState({}, '', '/accounting');
  server.use(http.get('*/api/beta/system', () => HttpResponse.json({ name: 'Workbench', version: 'test' })), http.get('*/api/beta/auth/me', () => HttpResponse.json({ userId: 'user', email: 'user@example.com', tenantName: 'Studio', permissions })));
  try {
    // WHEN opening an accounting deep link THEN setup is denied without fetching private data
    render(<App />);
    expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeVisible();
    expect(screen.queryByRole('link', { name: 'Accounting' })).not.toBeInTheDocument();
  } finally { window.history.replaceState({}, '', '/'); }
});
