import { act, fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { getDrafts } from '../../api/purchaseOrders';
import { getSuppliers } from '../../api/suppliers';
import { DraftList } from './DraftList';
import { DraftMemory } from './draftMemory';
import { SupplierList } from './SupplierList';

vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), getDrafts: vi.fn() }));
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), getSuppliers: vi.fn() }));
beforeEach(() => { vi.useFakeTimers(); vi.mocked(getDrafts).mockReset(); vi.mocked(getSuppliers).mockReset(); });
afterEach(() => vi.useRealTimers());

for (const kind of ['orders', 'suppliers'] as const) {
  const api = kind === 'orders' ? vi.mocked(getDrafts) : vi.mocked(getSuppliers);
  async function mount() {
    api.mockResolvedValue({ items: [], nextCursor: null });
    const view = render(kind === 'orders'
      ? <DraftList memory={new DraftMemory()} follow={vi.fn()} onAuthLost={vi.fn()} />
      : <SupplierList onAuthLost={vi.fn()} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    return view;
  }
  it(`${kind}: searches only after typing pauses and cancels pending work on departure`, async () => {
    // GIVEN a loaded purchasing list.
    const view = await mount();
    const field = screen.getByRole('searchbox');
    // WHEN typing continues before the 300 ms delay expires.
    fireEvent.change(field, { target: { value: 'Ge' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    fireEvent.change(field, { target: { value: ' Gem ' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(299); });
    expect(api).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    // THEN one normalized search is sent without a Search button or Enter.
    expect(api).toHaveBeenCalledTimes(2);
    expect(api.mock.calls[1][1]).toBe('Gem');
    expect(screen.queryByRole('button', { name: 'Search' })).not.toBeInTheDocument();
    // WHEN leaving with another debounce pending THEN no request is sent afterward.
    fireEvent.change(field, { target: { value: 'Other' } });
    view.unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(api).toHaveBeenCalledTimes(2);
  });
  it(`${kind}: clearing immediately supersedes a pending debounce without a duplicate request`, async () => {
    // GIVEN a query whose debounce has not fired.
    await mount();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'Gem' } });
    // WHEN clearing the field explicitly THEN the unfiltered list loads immediately once.
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Clear search' })); });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(api).toHaveBeenCalledTimes(2);
    expect(api.mock.calls[1][1]).toBeUndefined();
    expect(screen.getByRole('searchbox')).toHaveValue('');
  });
  it(`${kind}: invalidates older responses as soon as new typing starts`, async () => {
    // GIVEN a search request still in flight.
    await mount();
    let reject!: (reason: Error) => void;
    api.mockImplementationOnce(() => new Promise<never>((_, fail) => { reject = fail; }));
    const field = screen.getByRole('searchbox');
    fireEvent.change(field, { target: { value: 'old' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    // WHEN a new query is being debounced and the older request fails.
    fireEvent.change(field, { target: { value: 'new' } });
    await act(async () => reject(new Error('obsolete failure')));
    // THEN the obsolete response cannot change feedback while the new search is pending.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('status')).toBeVisible();
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(api.mock.calls.at(-1)?.[1]).toBe('new');
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
  it(`${kind}: Enter searches immediately and cancels the delayed duplicate`, async () => {
    // GIVEN typing has scheduled a search.
    await mount();
    const field = screen.getByRole('searchbox');
    fireEvent.change(field, { target: { value: 'Gem' } });
    // WHEN pressing Enter THEN the query is sent immediately, once.
    await act(async () => { fireEvent.submit(field.closest('form')!); });
    expect(api).toHaveBeenCalledTimes(2);
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(api).toHaveBeenCalledTimes(2);
  });
}
