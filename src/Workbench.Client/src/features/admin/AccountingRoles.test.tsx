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
it('releases uncertain retry state after a conflict and reload while retaining the selection', async () => {
  // GIVEN an assignment whose first response is lost and whose retry conflicts
  const requests: Array<{ requestId: string; expectedVersion: string; roleIds: string[] }> = [];
  let reads = 0;
  server.use(
    http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: reader, name: 'Accounting reader', permissions: ['AccountingReportsRead'] }])),
    http.get('*/api/beta/tenant/users/:userId/accounting-roles', () => HttpResponse.json({ userId: 'user', roleIds: [], version: ++reads === 1 ? 'v1' : 'v2' })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/tenant/users/:userId/accounting-roles', async ({ request }) => {
      requests.push(await request.json() as typeof requests[number]);
      if (requests.length === 1) return HttpResponse.error();
      if (requests.length === 2) return new HttpResponse(null, { status: 409 });
      return HttpResponse.json({ userId: 'user', roleIds: [], version: 'v3' });
    }),
  );
  render(<AccountingRoles userId="user" email="member@example.com" close={vi.fn()} />);
  fireEvent.click(await screen.findByLabelText('Accounting reader'));
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await screen.findByText('Roles could not be saved. Retry the same assignment.');
  expect(screen.getByLabelText('Accounting reader')).toBeDisabled();
  // WHEN retrying and explicitly reloading the current assignment
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Reload roles and retain selection' }));
  await screen.findByText('Current roles loaded. Review proposed changes before saving.');
  // THEN the retained selection is editable again and future saves use a fresh request/version
  expect(screen.getByLabelText('Accounting reader')).toBeChecked();
  expect(screen.getByLabelText('Accounting reader')).toBeEnabled();
  expect(screen.getByRole('button', { name: 'Close roles' })).toBeEnabled();
  expect(requests[1]).toEqual(requests[0]);
  fireEvent.click(screen.getByLabelText('Accounting reader'));
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await waitFor(() => expect(requests).toHaveLength(3));
  expect(requests[2]).toMatchObject({ expectedVersion: 'v2', roleIds: [] });
  expect(requests[2].requestId).not.toBe(requests[0].requestId);
});
it('reads current membership after a historical replay receipt instead of resurrecting its grants', async () => {
  // GIVEN a replay receipt for a grant that another administrator has since revoked
  let reads = 0;
  server.use(
    http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: reader, name: 'Accounting reader', permissions: ['AccountingReportsRead'] }])),
    http.get('*/api/beta/tenant/users/:userId/accounting-roles', () => HttpResponse.json({ userId: 'user', roleIds: [], version: ++reads === 1 ? 'v1' : 'v3' })),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/tenant/users/:userId/accounting-roles', () => HttpResponse.json({ userId: 'user', roleIds: [reader], version: 'v2' })),
  );
  render(<AccountingRoles userId="user" email="member@example.com" close={vi.fn()} />);
  fireEvent.click(await screen.findByLabelText('Accounting reader'));
  // WHEN the command succeeds THEN the editor shows current membership from readback
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await screen.findByText('Accounting roles saved. Changes take effect on the next request.');
  expect(reads).toBe(2);
  expect(screen.getByLabelText('Accounting reader')).not.toBeChecked();
});

it('retains the original role command when successful receipt readback is unavailable', async () => {
  // GIVEN a successful command followed by a lost current-membership response
  const requests: Array<{ requestId: string; expectedVersion: string; roleIds: string[] }> = [];
  let reads = 0;
  server.use(
    http.get('*/api/beta/tenant/accounting-roles', () => HttpResponse.json([{ id: reader, name: 'Accounting reader', permissions: ['AccountingReportsRead'] }])),
    http.get('*/api/beta/tenant/users/:userId/accounting-roles', () => {
      if (++reads === 2) return HttpResponse.error();
      return HttpResponse.json({ userId: 'user', roleIds: reads === 1 ? [] : [reader], version: reads === 1 ? 'v1' : 'v2' });
    }),
    http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })),
    http.post('*/api/beta/tenant/users/:userId/accounting-roles', async ({ request }) => {
      requests.push(await request.json() as typeof requests[number]);
      return HttpResponse.json({ userId: 'user', roleIds: [reader], version: 'v2' });
    }),
  );
  render(<AccountingRoles userId="user" email="member@example.com" close={vi.fn()} />);
  fireEvent.click(await screen.findByLabelText('Accounting reader'));
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await screen.findByText('Roles could not be saved. Retry the same assignment.');
  expect(screen.getByRole('button', { name: 'Close roles' })).toBeDisabled();
  // WHEN retried THEN it reuses the accepted command until current readback completes
  fireEvent.click(screen.getByRole('button', { name: 'Save accounting roles' }));
  await screen.findByText('Accounting roles saved. Changes take effect on the next request.');
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  expect(screen.getByRole('button', { name: 'Close roles' })).toBeEnabled();
});
