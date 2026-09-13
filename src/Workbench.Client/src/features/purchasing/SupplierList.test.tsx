import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { getSuppliers, SupplierError } from '../../api/suppliers';
import { SupplierList } from './SupplierList';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), getSuppliers: vi.fn() }));
const row = (id: string, isArchived = false) => ({ id, supplier: { name: id, contactName: 'Owner', email: `${id}@example.test`, phone: null, website: null, postalAddress: null }, isArchived, version: 'v1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' });
beforeEach(() => { vi.mocked(getSuppliers).mockReset(); });
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
  const onAuthLost = vi.fn(); render(<SupplierList follow={vi.fn()} onAuthLost={onAuthLost} />); await screen.findByText('Private');
  // WHEN refreshing fails authorization THEN contact details disappear before auth refresh completes.
  fireEvent.click(screen.getByRole('button', { name: 'Refresh suppliers' })); await waitFor(() => expect(onAuthLost).toHaveBeenCalled()); expect(screen.queryByText('Private')).not.toBeInTheDocument(); expect(screen.queryByText('Private@example.test')).not.toBeInTheDocument();
});
