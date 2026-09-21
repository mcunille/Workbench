import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { AccountingRoles } from './AccountingRoles';
const admin = '11111111-1111-1111-1111-111111111111';
const reader = '22222222-2222-2222-2222-222222222222';
it('previews grants and sends only the selected fixed roles with the expected version', async () => {
  // GIVEN two role definitions and no current accounting assignment
  let command: unknown;
  server.use(http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: admin, name: 'Accounting administrator', permissions: ['AccountingConfigurationManage'] }, { id: reader, name: 'Accounting reader', permissions: ['AccountingReportsRead'] }])), http.get('*/api/beta/tenant/users/:userId/accounting-roles', () => HttpResponse.json({ userId: 'user', roleIds: [], version: 'v1' })), http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })), http.post('*/api/beta/tenant/users/:userId/accounting-roles', async ({ request }) => { command = await request.json(); return HttpResponse.json({ userId: 'user', roleIds: [reader], version: 'v2' }); }));
  render(<AccountingRoles userId="user" email="member@example.com" close={vi.fn()} />);
  // WHEN selecting read authority THEN the proposed grant is visible before writing
  fireEvent.click(await screen.findByLabelText('Accounting reader'));
  expect(screen.getByText('Proposed grants: Accounting reader. Proposed revocations: None.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await waitFor(() => expect(command).toMatchObject({ expectedVersion: 'v1', roleIds: [reader] }));
  expect(command).not.toHaveProperty('permissions');
});
