import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { App } from './App';
import { server } from './test/server';

it('keeps role-save feedback while refreshing newly granted accounting navigation', async () => {
  // GIVEN a tenant administrator editing their own roles
  let assigned = false; let refreshed = false;
  let release!: () => void;
  const identityRefresh = new Promise<void>(resolve => { release = resolve; });
  window.history.replaceState({}, '', '/administration');
  server.use(
    http.get('*/api/beta/system', () => HttpResponse.json({ name: 'Workbench', version: 'test' })),
    http.get('*/api/beta/auth/me', async () => { if (assigned) { refreshed = true; await identityRefresh; } return HttpResponse.json({ userId: 'user', email: 'admin@example.com', tenantName: 'Studio', permissions: ['TenantAccess', 'TenantUsersManage', ...(assigned ? ['AccountingConfigurationRead'] : [])] }); }),
    http.get('*/api/beta/tenant/users', () => HttpResponse.json([{ id: 'user', email: 'admin@example.com', state: 1 }])),
    http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: 'administrator', name: 'Accounting administrator', permissions: ['AccountingConfigurationRead'] }])),
    http.get('*/api/beta/tenant/users/:userId/accounting-roles', () => HttpResponse.json({ userId: 'user', roleIds: assigned ? ['administrator'] : [], version: assigned ? 'v2' : 'v1' })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/tenant/users/:userId/accounting-roles', () => { assigned = true; return HttpResponse.json({ userId: 'user', roleIds: ['administrator'], version: 'v2' }); }),
  );
  try {
    render(<App />);
    fireEvent.click(await screen.findByRole('button', { name: 'Accounting roles' }));
    fireEvent.click(await screen.findByLabelText('Accounting administrator'));
    // WHEN the assignment saves THEN refreshed permissions update navigation without discarding the editor
    fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
    await waitFor(() => expect(refreshed).toBe(true));
    expect(screen.getByText('Accounting roles saved. Changes take effect on the next request.')).toBeVisible();
    await act(async () => release());
    expect(await screen.findByRole('link', { name: 'Accounting' })).toBeVisible();
    expect(screen.getByText('Accounting roles saved. Changes take effect on the next request.')).toBeVisible();
    expect(screen.getByLabelText('Accounting administrator')).toBeChecked();
  } finally { release(); window.history.replaceState({}, '', '/'); }
});


