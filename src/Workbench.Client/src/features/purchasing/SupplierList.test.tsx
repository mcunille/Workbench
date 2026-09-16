import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { getSuppliers, SupplierError } from '../../api/suppliers';
import { SupplierList } from './SupplierList';
import { SupplierMemory } from './supplierMemory';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), getSuppliers: vi.fn() }));
const row = (id: string, isArchived = false) => ({ id, supplier: { name: id, contactName: 'Owner', email: `${id}@example.test`, phone: null, website: null, postalAddress: null }, isArchived, version: 'v1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' });
beforeEach(() => { vi.mocked(getSuppliers).mockReset(); });
it('restores the directory query, archive filter, loaded rows and scroll without affecting pickers', async () => {
  // GIVEN a directory view left for a supplier editor.
  const memory = new SupplierMemory(); memory.save({ items: [row('Gems', true)], nextCursor: 'more' }, 'Gems', true); memory.savePosition(420);
  vi.mocked(getSuppliers).mockResolvedValueOnce(memory.page!);
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  const view = render(<SupplierList memory={memory} follow={vi.fn()} onAuthLost={vi.fn()} />);
  // WHEN returning THEN its context is restored without replacing the loaded page.
  expect(screen.getByRole('searchbox')).toHaveValue('Gems');
  expect(screen.getByLabelText('Include archived suppliers')).toBeChecked();
  expect(screen.getByRole('link', { name: 'Edit Gems' })).toBeVisible();
  expect(scroll).toHaveBeenCalledWith(0, 420);
  await waitFor(() => expect(getSuppliers).toHaveBeenCalledWith(undefined, 'Gems', true));
  view.unmount();
  // AND a picker starts fresh rather than inheriting directory filters or cached archived rows.
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Fresh')], nextCursor: null });
  render(<SupplierList memory={memory} onSelect={vi.fn()} onAuthLost={vi.fn()} />);
  expect(screen.getByRole('searchbox')).toHaveValue('');
  expect(await screen.findByRole('button', { name: 'Select Fresh' })).toBeEnabled();
  scroll.mockRestore();
});
it('refreshes the previously loaded extent after a saved supplier changes', async () => {
  // GIVEN two pages retained after a confirmed supplier edit invalidated their data.
  const memory = new SupplierMemory(); memory.save({ items: [row('Old'), row('Second')], nextCursor: 'old-next' }, 'Gem', true); memory.savePosition(200); memory.invalidate();
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Updated')], nextCursor: 'fresh-next' }).mockResolvedValueOnce({ items: [row('Second')], nextCursor: 'remaining' });
  render(<SupplierList memory={memory} follow={vi.fn()} onAuthLost={vi.fn()} />);
  // WHEN returning THEN fresh pages replace stale details while preserving the search, filter, and extent.
  await screen.findByText('Updated');
  expect(screen.getByText('Second')).toBeVisible();
  expect(screen.queryByText('Old')).not.toBeInTheDocument();
  expect(getSuppliers).toHaveBeenNthCalledWith(1, undefined, 'Gem', true);
  expect(getSuppliers).toHaveBeenNthCalledWith(2, 'fresh-next', 'Gem', true);
  expect(memory.page?.nextCursor).toBe('remaining'); expect(memory.needsRefresh).toBe(false);
  expect(memory.scrollY).toBe(200);
});
it('makes the whole directory row a native link', async () => {
  // GIVEN one supplier WHEN the directory loads THEN name and contact details share its navigation link.
  vi.mocked(getSuppliers).mockResolvedValue({ items: [row('Gems')], nextCursor: null });
  render(<SupplierList follow={vi.fn()} onAuthLost={vi.fn()} />);
  const name = await screen.findByText('Gems');
  expect(name.closest('a')).toHaveAttribute('href', '/suppliers/Gems');
  expect(screen.getByText('Owner · Gems@example.test').closest('a')).toBe(name.closest('a'));
});
it('announces results and returns keyboard focus to search when cleared', async () => {
  // GIVEN a completed search with more results available.
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Gems')], nextCursor: 'more' }).mockResolvedValueOnce({ items: [], nextCursor: null });
  render(<SupplierList follow={vi.fn()} onAuthLost={vi.fn()} />);
  await screen.findByText('Gems');
  expect(screen.getByRole('status')).toHaveTextContent('Suppliers shown: 1. More available.');
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'no match' } });
  fireEvent.submit(screen.getByRole('searchbox').closest('form')!);
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('No matching suppliers.'));
  // WHEN clearing through the keyboard action THEN focus returns to search and empty completion is announced.
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [], nextCursor: null });
  const clear = screen.getByRole('button', { name: 'Clear search' }); clear.focus(); fireEvent.click(clear);
  expect(screen.getByRole('searchbox')).toHaveFocus();
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('No suppliers yet.'));
});
it('retains query-bound loaded pages on failure and ignores obsolete responses', async () => {
  // GIVEN directory results whose continuation finishes after a new search.
  let finish!: (value: { items: ReturnType<typeof row>[]; nextCursor: null }) => void;
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Original')], nextCursor: 'cursor' }).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce({ items: [row('Latest')], nextCursor: 'latest-cursor' }).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ items: [row('More')], nextCursor: null });
  render(<SupplierList follow={vi.fn()} onAuthLost={vi.fn()} />); fireEvent.click(await screen.findByRole('button', { name: 'Load more suppliers' }));
  // WHEN searching supersedes the outstanding continuation, then another query fails.
  const search = screen.getByRole('searchbox'); fireEvent.change(search, { target: { value: 'Latest' } }); fireEvent.submit(search.closest('form')!);
  await screen.findByText('Latest'); await act(async () => finish({ items: [row('Obsolete')], nextCursor: null })); expect(screen.queryByText('Obsolete')).not.toBeInTheDocument();
  fireEvent.change(search, { target: { value: 'Failed' } }); fireEvent.submit(search.closest('form')!); await screen.findByRole('alert');
  fireEvent.click(screen.getByRole('button', { name: 'Load more suppliers' }));
  // THEN retained pagination uses the loaded query and preserves earlier rows.
  await screen.findByText('More'); expect(screen.getByText('Latest')).toBeVisible(); expect(getSuppliers).toHaveBeenLastCalledWith('latest-cursor', 'Latest', false);
});
it('makes archived inclusion explicit and never offers archived supplier selection', async () => {
  // GIVEN an archived supplier returned for an explicit inclusion request.
  vi.mocked(getSuppliers).mockResolvedValue({ items: [row('Archived', true)], nextCursor: null });
  const view = render(<SupplierList follow={vi.fn()} onAuthLost={vi.fn()} />); await screen.findByRole('link', { name: 'Edit Archived' });
  // WHEN including archived records THEN the server receives the filter.
  fireEvent.click(screen.getByLabelText('Include archived suppliers')); await waitFor(() => expect(getSuppliers).toHaveBeenLastCalledWith(undefined, undefined, true));
  view.unmount(); render(<SupplierList onSelect={vi.fn()} onAuthLost={vi.fn()} />);
  // THEN selection is disabled even if an archived record is unexpectedly returned by the server.
  expect(await screen.findByRole('button', { name: 'Select Archived' })).toBeDisabled(); expect(screen.queryByLabelText('Include archived suppliers')).not.toBeInTheDocument();
});
it('clears private rows when access is lost', async () => {
  // GIVEN loaded private contacts followed by a business access failure.
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Private')], nextCursor: null }).mockRejectedValueOnce(new SupplierError(403));
  const memory = new SupplierMemory();
  const onAuthLost = vi.fn(); render(<SupplierList memory={memory} follow={vi.fn()} onAuthLost={onAuthLost} />); await screen.findByText('Private');
  // WHEN refreshing fails authorization THEN contact details disappear before auth refresh completes.
  fireEvent.click(screen.getByRole('button', { name: 'Refresh suppliers' })); await waitFor(() => expect(onAuthLost).toHaveBeenCalled()); expect(screen.queryByText('Private')).not.toBeInTheDocument(); expect(screen.queryByText('Private@example.test')).not.toBeInTheDocument();
  expect(memory.page).toBeUndefined(); expect(memory.query).toBe('');
});
it('keeps the saved directory context when refreshing a later page fails', async () => {
  // GIVEN a saved contact change and two previously loaded pages.
  const memory = new SupplierMemory(); memory.save({ items: [row('Old'), row('Second')], nextCursor: null }, 'Gem', true); memory.invalidate();
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Updated')], nextCursor: 'more' }).mockRejectedValueOnce(new TypeError('Network'));
  render(<SupplierList memory={memory} onAuthLost={vi.fn()} />);
  // WHEN refreshing the second page fails THEN the old view remains complete and retryable.
  await screen.findByRole('alert');
  expect(screen.getByText('Old')).toBeVisible(); expect(screen.getByText('Second')).toBeVisible();
  expect(memory.needsRefresh).toBe(true);
  vi.mocked(getSuppliers).mockResolvedValueOnce({ items: [row('Updated')], nextCursor: 'more' }).mockResolvedValueOnce({ items: [row('Second')], nextCursor: null });
  // WHEN retrying THEN the complete refreshed extent replaces the old view.
  fireEvent.click(screen.getByRole('button', { name: 'Refresh suppliers' }));
  await screen.findByText('Updated');
  expect(screen.getByText('Second')).toBeVisible(); expect(memory.needsRefresh).toBe(false);
});
