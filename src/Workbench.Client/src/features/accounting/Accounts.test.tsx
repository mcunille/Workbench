import { fireEvent, render, screen } from '@testing-library/react';
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
