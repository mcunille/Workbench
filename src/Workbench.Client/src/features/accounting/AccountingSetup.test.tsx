import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { AccountingSetup } from './AccountingSetup';

const configuration = { policies: { country: null, region: null, currency: null, scale: null, fiscalStartMonth: null, startApproach: null, plannedStartDate: null, retentionYears: null, retentionRationale: null, frameworkNotes: null }, mappings: [], coverage: [] };
const setup = { configuration, version: 'v1', setupComplete: false, bookkeepingAvailable: false, missingItems: ['Currency'], blockers: ['Journal is not available.'] };
const catalog = { version: '1', countries: [{ code: 'US', name: 'United States', regions: [{ code: 'WA', name: 'Washington' }] }], currencies: [{ code: 'USD', name: 'US dollar', scale: 2 }], accountTypes: ['Asset','Liability','Equity','Income','Expense'], accountPurposes: ['General','Bank'], mappingSlots: ['SupplierPayable'], starterAccounts: [{ code: '1000', name: 'Bank', type: 'Asset', purpose: 'Bank' }] };
function handlers() {
  server.use(http.get('*/api/beta/accounting/catalog', () => HttpResponse.json(catalog)), http.get('*/api/beta/accounting/setup', () => HttpResponse.json(setup)), http.get('*/api/beta/accounting/accounts', () => HttpResponse.json({ items: [], nextCursor: null })), http.get('*/api/beta/auth/antiforgery', () => HttpResponse.json({ requestToken: 'test' })));
}
describe('Accounting setup', () => {
  it('saves explicit business choices without inferring precision or enabling bookkeeping', async () => {
    // GIVEN incomplete accounting setup and an authorized administrator
    handlers(); let saved: unknown;
    server.use(http.put('*/api/beta/accounting/setup', async ({ request }) => { saved = await request.json(); return HttpResponse.json({ savedVersion: 'v2', accountIds: [] }); }));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // WHEN currency is selected THEN precision remains an explicit decision
    fireEvent.change(await screen.findByLabelText('Functional currency'), { target: { value: 'USD' } });
    expect(screen.getByLabelText('Posting decimal places')).toHaveValue('');
    fireEvent.change(screen.getByLabelText('Posting decimal places'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    // THEN the versioned command contains the explicit policy and no activation flag
    await waitFor(() => expect(saved).toMatchObject({ expectedVersion: 'v1', configuration: { policies: { currency: 'USD', scale: 2 } } }));
    expect(saved).not.toHaveProperty('bookkeepingAvailable');
    expect(await screen.findByText('Accounting setup saved. Bookkeeping is not yet available.')).toBeVisible();
  });
  it('retains a stale draft until the administrator explicitly reconciles', async () => {
    // GIVEN another administrator has saved a newer version
    handlers(); let reads = 0;
    server.use(http.get('*/api/beta/accounting/setup', () => HttpResponse.json({ ...setup, version: ++reads > 1 ? 'v2' : 'v1' })), http.put('*/api/beta/accounting/setup', () => new HttpResponse(null, { status: 409 })));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // WHEN saving a stale draft THEN it is retained and further saves are blocked pending reconciliation
    fireEvent.change(await screen.findByLabelText('Functional currency'), { target: { value: 'USD' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    expect(await screen.findByRole('heading', { name: 'Resolve concurrent changes' })).toBeVisible();
    expect(screen.getByLabelText('Functional currency')).toHaveValue('USD');
    expect(screen.getByRole('button', { name: 'Save setup' })).toBeDisabled();
  });
  it('clears private configuration when a save reveals revoked access', async () => {
    // GIVEN accounting access has been revoked after the setup was loaded
    handlers(); const lost = vi.fn();
    server.use(http.put('*/api/beta/accounting/setup', () => new HttpResponse(null, { status: 403 })));
    render(<AccountingSetup canManage onAuthLost={lost} onDirtyChange={vi.fn()} />);
    // WHEN saving THEN the private form is removed
    fireEvent.change(await screen.findByLabelText('Framework and tax policy notes (optional)'), { target: { value: 'Private plan' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeVisible();
    expect(screen.queryByDisplayValue('Private plan')).not.toBeInTheDocument();
    expect(lost).toHaveBeenCalled();
  });
  it('requires a preview before starter accounts are created', async () => {
    // GIVEN no accounts exist
    handlers(); let requests = 0;
    server.use(http.post('*/api/beta/accounting/accounts', () => { requests++; return HttpResponse.json({ savedVersion: 'v2', accountIds: [] }); }));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Accounts and mappings' }));
    // WHEN previewing THEN nothing is created until explicit confirmation
    fireEvent.click(screen.getByRole('button', { name: 'Preview starter chart' }));
    expect(screen.getByText('1000 — Bank')).toBeVisible(); expect(requests).toBe(0);
    fireEvent.click(screen.getByRole('button', { name: 'Create these accounts' }));
    await waitFor(() => expect(requests).toBe(1));
  });
});
