import { act, renderHook } from '@testing-library/react';
import { vi } from 'vitest';
import { calculateDraft, DraftError, type DraftContent, type DraftCalculation } from '../../api/purchaseOrders';
import { useDraftCalculation } from './useDraftCalculation';
import { emptyLine } from './draftLine';
vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), calculateDraft: vi.fn() }));
const draft: DraftContent = { title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [emptyLine('line')] };
const result: DraftCalculation = { lines: [{ id: 'line', gross: '200.0000' }], incompleteLineCount: 0, merchandiseEstimate: '200.0000' };
beforeEach(() => { vi.useFakeTimers(); vi.mocked(calculateDraft).mockReset(); });
afterEach(() => vi.useRealTimers());
it('aborts obsolete requests and never displays their results for newer input', async () => {
  // GIVEN a calculation whose response will arrive after a subsequent edit.
  let resolveOld!: (value: DraftCalculation) => void;
  vi.mocked(calculateDraft).mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; })).mockResolvedValueOnce({ ...result, merchandiseEstimate: '250.0000' });
  const lost = vi.fn();
  const view = renderHook(({ content }) => useDraftCalculation(content, true, lost), { initialProps: { content: draft } });
  await act(() => vi.advanceTimersByTimeAsync(300));
  const signal = vi.mocked(calculateDraft).mock.calls[0][1]!;
  // WHEN new input arrives, then the older response arrives before the new debounce completes.
  view.rerender({ content: { ...draft, title: 'Changed' } });
  await act(async () => resolveOld(result));
  // THEN old results cannot masquerade as current values and their request is cancelled.
  expect(signal.aborted).toBe(true);
  expect(view.result.current.result).toBeUndefined();
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(view.result.current.result?.merchandiseEstimate).toBe('250.0000');
});
it('hides an earlier estimate immediately on edits and retains recoverable validation paths', async () => {
  // GIVEN a successful estimate followed by invalid updated input.
  vi.mocked(calculateDraft).mockResolvedValueOnce(result).mockRejectedValueOnce(new DraftError(400, 'draft_validation_failed', { 'draft.entries[0].quantity': ['Use a positive quantity.'] }));
  const lost = vi.fn();
  const view = renderHook(({ content }) => useDraftCalculation(content, true, lost), { initialProps: { content: draft } });
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(view.result.current.result).toEqual(result);
  // WHEN the draft changes THEN stale figures disappear before another response.
  view.rerender({ content: { ...draft, entries: [{ ...draft.entries[0], quantity: '0' }] } });
  expect(view.result.current.result).toBeUndefined();
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(view.result.current.errors?.['draft.entries[0].quantity']).toEqual(['Use a positive quantity.']);
  expect(view.result.current.message).toContain('Review');
});
it('recovers from a calculation outage without submitting a save', async () => {
  // GIVEN a failed calculation followed by restored service.
  vi.mocked(calculateDraft).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce(result);
  const view = renderHook(() => useDraftCalculation(draft, true, vi.fn()));
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(view.result.current.message).toContain('Your changes are kept');
  // WHEN the owner retries THEN only the calculation is requested again.
  act(() => view.result.current.retry());
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(view.result.current.result).toEqual(result);
  expect(calculateDraft).toHaveBeenCalledTimes(2);
});
it('clears access on authentication loss and suppresses requests while editing is frozen', async () => {
  // GIVEN a frozen editor, then expired authentication when editing resumes.
  const lost = vi.fn();
  vi.mocked(calculateDraft).mockRejectedValue(new DraftError(401));
  const view = renderHook(({ enabled }) => useDraftCalculation(draft, enabled, lost), { initialProps: { enabled: false } });
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(calculateDraft).not.toHaveBeenCalled();
  // WHEN editing resumes THEN authentication failure invokes private-state cleanup.
  view.rerender({ enabled: true });
  await act(() => vi.advanceTimersByTimeAsync(300));
  expect(lost).toHaveBeenCalledTimes(1);
  expect(view.result.current.result).toBeUndefined();
});
