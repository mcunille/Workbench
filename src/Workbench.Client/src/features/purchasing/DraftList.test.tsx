import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { getDrafts, DraftError } from '../../api/purchaseOrders';
import { DraftList } from './DraftList';
import { DraftMemory } from './draftMemory';
vi.mock('../../api/purchaseOrders', async importOriginal => ({ ...await importOriginal<typeof import('../../api/purchaseOrders')>(), getDrafts: vi.fn() }));
const row = (id: string) => ({ id, title: id, supplierName: null, firstItemDescription: null, poReference: 'PO-000001', supplierOrderReference: null, platform: null, updatedAtUtc: '2026-09-12T00:00:00Z' });
const props = () => ({ memory: new DraftMemory(), follow: vi.fn(), onAuthLost: vi.fn() });
beforeEach(() => { vi.mocked(getDrafts).mockReset(); });
it('wires accessible purchase references to distinct records from the same supplier', async () => {
  // GIVEN identically labelled purchases from one supplier and a custom title on a third.
  const items = [
    { ...row('one'), title: null, supplierName: 'Gem supplier', poReference: 'PO-000001' },
    { ...row('two'), title: null, supplierName: 'Gem supplier', poReference: 'PO-000002' },
    { ...row('named'), title: 'Autumn collection', supplierName: 'Gem supplier', poReference: 'PO-000003' },
  ];
  vi.mocked(getDrafts).mockResolvedValue({ items, nextCursor: null });
  // WHEN browsing THEN each accessible reference identifies the right record and display label.
  render(<DraftList {...props()} />);
  expect(await screen.findByRole('link', { name: /PO-000001.*Gem supplier/ })).toHaveAttribute('href', '/purchase-orders/one');
  expect(screen.getByRole('link', { name: /PO-000002.*Gem supplier/ })).toHaveAttribute('href', '/purchase-orders/two');
  expect(screen.getByRole('link', { name: /PO-000003.*Autumn collection.*Gem supplier/ })).toHaveAttribute('href', '/purchase-orders/named');
  expect(screen.queryByText(/Untitled/)).not.toBeInTheDocument();
});
it('distinguishes ordered purchases from drafts and filters the server query by state', async () => {
  // GIVEN a unified page with both planned and placed purchases.
  vi.mocked(getDrafts).mockResolvedValue({ items: [{ ...row('planned'), state: 'Draft' }, { ...row('placed'), state: 'Ordered', orderDate: '2026-09-16', revision: 1 }], nextCursor: null });
  render(<DraftList {...props()} />);
  expect(await screen.findByRole('link', { name: /placed.*Ordered/ })).toBeVisible();
  // WHEN selecting ordered purchases THEN filtering is performed before pagination by the server.
  fireEvent.change(screen.getByLabelText('Order status'), { target: { value: 'Ordered' } });
  await screen.findByRole('link', { name: /placed.*Ordered/ });
  expect(getDrafts).toHaveBeenLastCalledWith(undefined, undefined, 'Ordered');
});
it('clears the status filter from page one while preserving search and keyboard focus', async () => {
  // GIVEN an ordered-only search with another page available.
  const callbacks = props();
  callbacks.memory.save({ items: [{ ...row('placed'), state: 'Ordered' }], nextCursor: 'ordered-page' }, 'sapphire', 'Ordered');
  vi.mocked(getDrafts).mockResolvedValue({ items: [row('planned')], nextCursor: null });
  render(<DraftList {...callbacks} />);
  expect(screen.getByRole('combobox', { name: 'Order status' })).toHaveValue('Ordered');
  // WHEN clearing only status THEN all states load from page one using the existing search.
  fireEvent.click(screen.getByRole('button', { name: 'Clear status filter' }));
  await screen.findByRole('link', { name: /planned/ });
  expect(getDrafts).toHaveBeenLastCalledWith(undefined, 'sapphire', undefined);
  expect(screen.getByRole('searchbox')).toHaveValue('sapphire');
  expect(screen.getByRole('combobox', { name: 'Order status' })).toHaveValue('');
  expect(screen.getByRole('combobox', { name: 'Order status' })).toHaveFocus();
  expect(screen.queryByRole('button', { name: 'Clear status filter' })).not.toBeInTheDocument();
  await waitFor(() => expect(callbacks.memory.state).toBe(''));
});
it('announces completed results and returns focus to search when cleared', async () => {
  // GIVEN a searched page with further results available.
  const callbacks = props(); callbacks.memory.save({ items: [row('kept')], nextCursor: 'next' }, 'gem');
  vi.mocked(getDrafts).mockResolvedValue({ items: [], nextCursor: null });
  render(<DraftList {...callbacks} />);
  expect(screen.getByRole('status')).toHaveTextContent('Purchase orders shown: 1. More available.');
  // WHEN the keyboard user clears the search.
  const clear = screen.getByRole('button', { name: 'Clear search' }); clear.focus(); fireEvent.click(clear);
  // THEN focus returns to the input and completion is announced without claiming a total.
  expect(screen.getByRole('searchbox')).toHaveFocus();
  await screen.findByRole('heading', { name: 'No purchase orders yet.' });
  expect(screen.getByRole('status')).toHaveTextContent('No purchase orders yet.');
});
it.each([false, true])('explains recovery when refreshing fails with retained results: %s', async retained => {
  // GIVEN either an initial load or a previously loaded search.
  const callbacks = props();
  if (retained) callbacks.memory.save({ items: [row('kept')], nextCursor: null }, 'old');
  vi.mocked(getDrafts).mockRejectedValue(new TypeError('Network'));
  render(<DraftList {...callbacks} />);
  // WHEN the request fails THEN retained results are explicitly identified and Refresh is the recovery action.
  if (retained) { fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'new' } }); fireEvent.submit(screen.getByRole('searchbox').closest('form')!); }
  expect(await screen.findByRole('alert')).toHaveTextContent(retained ? 'Showing previous results. Select Refresh to try again.' : 'Purchases could not be loaded. Select Refresh to try again.');
  expect(screen.getByRole('status')).toBeEmptyDOMElement();
  if (retained) { expect(screen.getByRole('searchbox')).toHaveValue('new'); expect(screen.getByRole('link', { name: /kept/ })).toBeVisible(); }
});
it('distinguishes empty loaded drafts from pending data', async () => {
  // GIVEN the first page is empty.
  vi.mocked(getDrafts).mockResolvedValue({ items: [], nextCursor: null }); render(<DraftList {...props()} />);
  // WHEN loading succeeds THEN the empty state offers creation, with no next page.
  await screen.findByRole('heading', { name: 'No purchase orders yet.' });
  expect(screen.getByRole('link', { name: 'New draft' })).toHaveAttribute('href', '/purchase-orders/new');
  expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
});
it('preserves a failed page cursor and deduplicates a successful retry', async () => {
  // GIVEN a loaded page and a transient error on its continuation.
  vi.mocked(getDrafts).mockResolvedValueOnce({ items: [row('one')], nextCursor: 'opaque' }).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ items: [row('one'), row('two')], nextCursor: null });
  render(<DraftList {...props()} />);
  // WHEN loading more fails and is retried.
  fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
  await screen.findByRole('alert'); expect(screen.getByRole('link', { name: /one/ })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
  // THEN the cursor is unchanged and duplicate records appear once.
  await screen.findByRole('link', { name: /two/ });
  expect(vi.mocked(getDrafts).mock.calls.map(call => call[0])).toEqual([undefined, 'opaque', 'opaque']);
  expect(screen.getAllByRole('link', { name: /one/ })).toHaveLength(1);
});
it('ignores an obsolete page after refresh and retains rows when refresh fails', async () => {
  // GIVEN load-more is pending when the user refreshes the list.
  let resolve!: (page: { items: ReturnType<typeof row>[]; nextCursor: null }) => void;
  vi.mocked(getDrafts).mockResolvedValueOnce({ items: [row('original')], nextCursor: 'old' }).mockImplementationOnce(() => new Promise(done => { resolve = done; })).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ items: [row('fresh')], nextCursor: null });
  render(<DraftList {...props()} />); fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
  // WHEN refresh fails THEN previously loaded rows stay visible.
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' })); await screen.findByRole('alert');
  expect(screen.getByRole('link', { name: /original/ })).toBeVisible();
  // WHEN the refresh succeeds and the older page later returns THEN the older result is ignored.
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' })); await screen.findByRole('link', { name: /fresh/ });
  await act(async () => resolve({ items: [row('obsolete')], nextCursor: null }));
  expect(screen.queryByRole('link', { name: /obsolete/ })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: /original/ })).not.toBeInTheDocument();
});
it('restores loaded pages on return and directs invalid cursors to refresh', async () => {
  // GIVEN the authenticated owner holds loaded pages from earlier navigation.
  const callbacks = props(); callbacks.memory.page = { items: [row('kept')], nextCursor: 'tampered' };
  vi.mocked(getDrafts).mockRejectedValue(new DraftError(400, 'invalid_cursor'));
  render(<DraftList {...callbacks} />);
  expect(getDrafts).not.toHaveBeenCalled();
  // WHEN continuation is rejected THEN keep rows and offer a first-page refresh.
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
  await screen.findByText('The next page is no longer available. Select Refresh to start again.');
  expect(screen.getByRole('link', { name: /kept/ })).toBeVisible();
});
it('searches the server from page one and displays permanent references and platforms', async () => {
  // GIVEN a loaded list and server matches outside its first page.
  vi.mocked(getDrafts).mockResolvedValueOnce({ items: [row('original')], nextCursor: 'old' }).mockResolvedValueOnce({ items: [{ ...row('matched'), poReference: 'PO-000042', platform: 'Instagram', supplierOrderReference: 'IG-7' }], nextCursor: null });
  render(<DraftList {...props()} />); await screen.findByRole('link', { name: /original/ });
  // WHEN the owner searches by supplier reference.
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search purchase orders' }), { target: { value: ' IG-7 ' } });
  fireEvent.submit(screen.getByRole('searchbox').closest('form')!);
  // THEN query-bound server results replace the old page and show identity and platform.
  await screen.findByText('PO-000042');
  expect(getDrafts).toHaveBeenLastCalledWith(undefined, 'IG-7', undefined);
  expect(screen.getByText(/Instagram/)).toBeVisible();
  expect(screen.queryByRole('link', { name: /original/ })).not.toBeInTheDocument();
});
it('does not let an older search overwrite a newer query and retains rows on a failed search', async () => {
  // GIVEN two searches whose responses arrive in reverse order.
  let finish!: (value: { items: ReturnType<typeof row>[]; nextCursor: null }) => void;
  vi.mocked(getDrafts).mockResolvedValueOnce({ items: [row('kept')], nextCursor: 'kept-cursor' }).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce({ items: [row('newest')], nextCursor: null }).mockRejectedValueOnce(new TypeError('Network'));
  render(<DraftList {...props()} />); await screen.findByRole('link', { name: /kept/ });
  const search = screen.getByRole('searchbox', { name: 'Search purchase orders' });
  // WHEN a second search supersedes the pending first one.
  fireEvent.change(search, { target: { value: 'first' } }); fireEvent.submit(search.closest('form')!);
  fireEvent.change(search, { target: { value: 'second' } }); fireEvent.submit(search.closest('form')!);
  await screen.findByRole('link', { name: /newest/ });
  await act(async () => finish({ items: [row('obsolete')], nextCursor: null }));
  // THEN only the latest results remain, including if the next search fails.
  expect(screen.queryByRole('link', { name: /obsolete/ })).not.toBeInTheDocument();
  fireEvent.change(search, { target: { value: 'failed' } }); fireEvent.submit(search.closest('form')!);
  await screen.findByRole('alert'); expect(screen.getByRole('link', { name: /newest/ })).toBeVisible();
});


it('continues the retained result query after a different search fails', async () => {
  // GIVEN query A rows remain visible when query B cannot load.
  const callbacks = props(); callbacks.memory.query = 'A'; callbacks.memory.page = { items: [row('kept')], nextCursor: 'cursor-A' };
  vi.mocked(getDrafts).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ items: [row('more-A')], nextCursor: null });
  render(<DraftList {...callbacks} />);
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'B' } }); fireEvent.submit(screen.getByRole('searchbox').closest('form')!); await screen.findByRole('alert');
  // WHEN continuing the retained results THEN their cursor stays bound to query A.
  fireEvent.click(screen.getByRole('button', { name: 'Load more' })); await screen.findByRole('link', { name: /more-A/ });
  expect(getDrafts).toHaveBeenLastCalledWith('cursor-A', 'A', undefined);
});
