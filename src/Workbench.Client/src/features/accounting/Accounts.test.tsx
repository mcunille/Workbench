import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { Accounts } from './Accounts';
const catalog = { version: '1', countries: [], currencies: [], accountTypes: ['Asset'], accountPurposes: ['General'], mappingSlots: [], starterAccounts: [] };
it('lets an administrator correct validation errors without replaying a rejected request', async () => {
  // GIVEN an account create command rejected for an occupied code
  server.use(http.get('*/api/beta/accounting/accounts', () => HttpResponse.json({ items: [], nextCursor: null })), http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })), http.post('*/api/beta/accounting/accounts', () => HttpResponse.json({ detail: 'Code is occupied.' }, { status: 400 })));
  render(<Accounts catalog={catalog} changed={async () => {}} fail={vi.fn()} canManage onDirtyChange={vi.fn()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create account' }));
  fireEvent.change(screen.getByLabelText('Account code'), { target: { value: '1000' } }); fireEvent.change(screen.getByLabelText('Account name'), { target: { value: 'Asset' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save account' }));
  // THEN the rejected command does not trap the user in uncertain-result recovery
  await screen.findByRole('status');
  expect(screen.getByRole('button', { name: 'Cancel' })).toBeEnabled();
});

it('shows current saved account fields beside the retained draft after a conflict', async () => {
  // GIVEN another administrator changes every descriptive field while this draft is open
  const original = { id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'General', description: 'Original description', version: 'v1', isArchived: false };
  const current = { ...original, code: '1010', name: 'Current bank', description: 'Other administrator description', version: 'v2' };
  let conflicted = false; const requests: unknown[] = [];
  server.use(http.get('*/api/beta/accounting/accounts', () => HttpResponse.json({ items: [conflicted ? current : original], nextCursor: null })), http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })), http.put('*/api/beta/accounting/accounts/bank', async ({ request }) => { requests.push(await request.json()); if (!conflicted) { conflicted = true; return new HttpResponse(null, { status: 409 }); } return HttpResponse.json({ savedVersion: 'v3', accountIds: ['bank'] }); }));
  render(<Accounts catalog={catalog} changed={async () => {}} fail={vi.fn()} canManage onDirtyChange={vi.fn()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
  fireEvent.change(screen.getByLabelText('Description (optional)'), { target: { value: 'My retained description' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save account' }));
  // WHEN reloading after a conflict THEN current saved values are available for comparison without replacing the draft
  fireEvent.click(await screen.findByRole('button', { name: 'Reload accounts and retain draft' }));
  const comparison = await screen.findByRole('region', { name: 'Current saved account' });
  for (const value of ['1010', 'Current bank', 'Other administrator description']) expect(within(comparison).getByText(value)).toBeVisible();
  expect(screen.getByLabelText('Account code')).toHaveValue('1000');
  expect(screen.getByLabelText('Description (optional)')).toHaveValue('My retained description');
  // WHEN explicitly saving the reviewed draft THEN it uses the refreshed version and retained fields
  fireEvent.click(screen.getByRole('button', { name: 'Save account' }));
  await waitFor(() => expect(requests).toHaveLength(2));
  expect(requests[1]).toMatchObject({ expectedVersion: 'v2', code: '1000', name: 'Bank', description: 'My retained description' });
  await screen.findByText('Accounts saved.');
  expect(screen.queryByRole('region', { name: 'Current saved account' })).not.toBeInTheDocument();
});
