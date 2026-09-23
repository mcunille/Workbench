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
  it.each([false, true])('marks pending saves uncertain for navigation (edited: %s)', async edited => {
    // GIVEN a setup save whose response has not arrived
    handlers(); const changed = vi.fn(); let release!: () => void;
    const pendingResponse = new Promise<void>(resolve => { release = resolve; });
    server.use(http.put('*/api/beta/accounting/setup', async () => { await pendingResponse; return HttpResponse.json({ savedVersion: 'v2', accountIds: [] }); }));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={changed} />);
    const notes = await screen.findByLabelText('Framework and tax policy notes (optional)');
    if (edited) fireEvent.change(notes, { target: { value: 'Pending policy' } });
    // WHEN saving THEN navigation must treat both unchanged and edited commands as uncertain
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    try { await waitFor(() => expect(changed).toHaveBeenLastCalledWith(true, true)); }
    finally { release(); await screen.findByText('Accounting setup saved. Bookkeeping is not yet available.'); }
    // AND a confirmed save clears the navigation warning
    await screen.findByText('Accounting setup saved. Bookkeeping is not yet available.');
    await waitFor(() => expect(changed).toHaveBeenLastCalledWith(false, false));
  });
  it('preserves a rejected draft and routes hidden policy errors to the field', async () => {
    // GIVEN a policy validation error returned while another section is open
    handlers();
    server.use(http.put('*/api/beta/accounting/setup', () => HttpResponse.json({ errors: { 'policies.retentionYears': ['Use 1–1000 years or leave unresolved.'] } }, { status: 400 })));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    fireEvent.change(await screen.findByLabelText('Proposed document retention (years)'), { target: { value: '1001' } });
    fireEvent.click(screen.getByRole('button', { name: 'Transaction coverage' }));
    // WHEN saving fails THEN the summary receives focus and keeps the draft
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    const summary = await screen.findByRole('region', { name: 'Review setup errors' });
    await waitFor(() => expect(summary).toHaveFocus());
    fireEvent.click(screen.getByRole('button', { name: /Proposed document retention.*Use 1–1000/ }));
    const field = screen.getByLabelText('Proposed document retention (years)');
    await waitFor(() => expect(field).toHaveFocus());
    expect(field).toHaveValue(1001);
    expect(field).toHaveAttribute('aria-invalid', 'true');
    expect(field).toHaveAccessibleDescription('Use 1–1000 years or leave unresolved.');
  });
  it('explains a hidden account edit and returns to it without losing the draft', async () => {
    // GIVEN an unfinished account edit
    handlers();
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Accounts and mappings' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create account' }));
    fireEvent.change(screen.getByLabelText('Account name'), { target: { value: 'Unsaved bank' } });
    // WHEN moving to policies THEN the blocked setup save explains how to recover
    fireEvent.click(screen.getByRole('button', { name: 'Policies' }));
    expect(screen.getByRole('button', { name: 'Save setup' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Return to account changes' }));
    expect(screen.getByLabelText('Account name')).toHaveValue('Unsaved bank');
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Create account' })).toHaveFocus());
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByRole('button', { name: 'Save setup' })).toBeEnabled();
  });
  it('allows discarding a known draft but preserves an uncertain save for an identical retry', async () => {
    // GIVEN an editable setup and a first save whose result is unknown
    handlers(); const requests: unknown[] = [];
    server.use(http.put('*/api/beta/accounting/setup', async ({ request }) => { requests.push(await request.json()); return requests.length === 1 ? new HttpResponse(null, { status: 500 }) : HttpResponse.json({ savedVersion: 'v2', accountIds: [] }); }));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    const notes = await screen.findByLabelText('Framework and tax policy notes (optional)');
    fireEvent.change(notes, { target: { value: 'Discard this draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'Discard setup changes' }));
    expect(notes).toHaveValue('');
    // WHEN a save fails ambiguously THEN discard is disabled and retry retains the command
    fireEvent.change(notes, { target: { value: 'Keep this draft' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save setup' }));
    const retry = await screen.findByRole('button', { name: 'Retry the same save' });
    expect(screen.getByRole('button', { name: 'Discard setup changes' })).toBeDisabled();
    expect(notes).toHaveValue('Keep this draft');
    expect(notes).toBeDisabled();
    expect(screen.getByText(/The save result is unknown/)).toBeVisible();
    fireEvent.click(retry);
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[1]).toEqual(requests[0]);
  });
  it('routes indexed coverage errors to the right account and preserves its help', async () => {
    // GIVEN account display order differs from the coverage command order
    handlers();
    const bank = { id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'Bank', description: null, isArchived: false, version: 'v1' };
    const cash = { ...bank, id: 'cash', code: '1010', name: 'Cash', purpose: 'Cash' };
    const coverage = [cash, bank].map(account => ({ accountId: account.id, included: true, attestedComplete: false, classes: [], evidenceKind: 'NoPriorActivity', fromDate: null, toDate: null, evidenceReference: null, rationale: null, exclusionRationale: null }));
    server.use(http.get('*/api/beta/accounting/accounts', () => HttpResponse.json({ items: [bank, cash], nextCursor: null })), http.get('*/api/beta/accounting/setup', () => HttpResponse.json({ ...setup, configuration: { ...configuration, coverage } })), http.put('*/api/beta/accounting/setup', () => HttpResponse.json({ errors: { 'coverage[0].evidenceKind': ['Provide a dated no-prior-activity declaration.'] } }, { status: 400 })));
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // WHEN saving from policies THEN the error targets Cash rather than the first displayed account
    fireEvent.click(await screen.findByRole('button', { name: 'Save setup' }));
    fireEvent.click(await screen.findByRole('button', { name: /1010 — Cash: Evidence basis/ }));
    const field = document.getElementById('accounting-coverage[0].evidenceKind');
    await waitFor(() => expect(field).toHaveFocus());
    expect(field?.closest('fieldset')).toHaveTextContent('1010 — Cash');
    expect(field).toHaveAccessibleDescription(/does not prove zero opening balances.*Provide a dated/);
  });
  it('explains both starting approaches without implying that setup creates balances', async () => {
    // GIVEN an administrator choosing how to start accounting
    handlers();
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // THEN decision support is associated with the choice and preserves the planning boundary
    expect(await screen.findByLabelText('Starting approach')).toHaveAccessibleDescription(/complete business history.*verified balances.*does not import history or create balances/i);
  });
  it('groups related policy decisions and provides direct access to mappings', async () => {
    // GIVEN an administrator preparing a business configuration
    handlers();
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // THEN related decisions are exposed as named groups in their reading order
    expect(await screen.findByRole('group', { name: 'Location and currency' })).toBeVisible();
    expect(screen.getByRole('group', { name: 'Accounting dates' })).toBeVisible();
    expect(screen.getByRole('group', { name: 'Retention and policy notes' })).toBeVisible();
    // WHEN browsing accounts THEN mappings have a direct keyboard-accessible destination
    fireEvent.click(screen.getByRole('button', { name: 'Accounts and mappings' }));
    const shortcut = screen.getByRole('link', { name: 'Go to mappings' });
    expect(shortcut).toHaveAttribute('href', '#accounting-mappings');
    expect(screen.getByRole('heading', { name: 'Mappings' })).toHaveAttribute('id', 'accounting-mappings');
    expect(screen.getByRole('heading', { name: 'Mappings' })).toHaveAttribute('tabindex', '-1');
  });
  it('keeps setup details collapsed while preserving unanswered decisions and capability limits', async () => {
    // GIVEN an incomplete setup with an unavailable bookkeeping capability
    handlers();
    render(<AccountingSetup canManage onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
    // WHEN the policies are ready THEN status details do not compete with the form
    expect(await screen.findByLabelText('Functional currency')).toBeVisible();
    const summary = screen.getByText('Saved setup: 1 unanswered item');
    const disclosure = summary.closest('details');
    expect(disclosure).not.toHaveAttribute('open');
    expect(disclosure).toHaveTextContent('Currency');
    expect(disclosure).toHaveTextContent('Journal is not available.');
    // WHEN details are expanded THEN both the remaining work and capability explanation remain available
    fireEvent.click(summary);
    expect(disclosure).toHaveAttribute('open');
    expect(screen.getByText('Journal is not available.')).toBeVisible();
  });
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
