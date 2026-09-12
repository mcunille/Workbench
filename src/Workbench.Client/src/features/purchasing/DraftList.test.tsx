import { act, fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { getDrafts, DraftError } from '../../api/purchaseOrders';
import { DraftList } from './DraftList';
import { DraftMemory } from './draftMemory';
vi.mock('../../api/purchaseOrders', async importOriginal => ({ ...await importOriginal<typeof import('../../api/purchaseOrders')>(), getDrafts: vi.fn() }));
const row = (id: string) => ({ id, title: id, supplierName: null, updatedAtUtc: '2026-09-12T00:00:00Z' });
const props = () => ({ memory: new DraftMemory(), follow: vi.fn(), onAuthLost: vi.fn() });
beforeEach(() => { vi.mocked(getDrafts).mockReset(); });
it('distinguishes empty loaded drafts from pending data', async () => {
  // GIVEN the first page is empty.
  vi.mocked(getDrafts).mockResolvedValue({ items: [], nextCursor: null }); render(<DraftList {...props()} />);
  // WHEN loading succeeds THEN the empty state offers creation, with no next page.
  await screen.findByText('No draft orders yet.');
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
  await screen.findByText('This page reference is no longer valid. Refresh drafts to start again.');
  expect(screen.getByRole('link', { name: /kept/ })).toBeVisible();
});
