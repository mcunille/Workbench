import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import { DraftEditor } from './DraftEditor';
import { getDraft, commitOrder, amendOrder, calculateDraft, getOrderRevisions, getOrderRevision, DraftError } from '../../api/purchaseOrders';
import { zeroAdjustmentCalculation } from '../../test/draftCalculationFixture';

vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), getDraft: vi.fn(), commitOrder: vi.fn(), amendOrder: vi.fn(), calculateDraft: vi.fn(), getOrderRevisions: vi.fn(), getOrderRevision: vi.fn() }));
const draft = { title: 'September stones', supplierName: 'Supplier', supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], orderDiscount: null, charges: [], entries: [{ id: 'line', description: 'Sapphires', quantity: '2', unitOfMeasure: 'piece', priceMode: 'perUnit', price: null, indicativePrice: null, legacyPricing: null, notes: null, sourceLink: null, supplierSku: null, itemType: null, discount: null }] };
const saved = { id: 'order', draft, poReference: 'PO-000001', supplierIsArchived: false, version: 'v1', createdAtUtc: '2026-09-17T00:00:00Z', updatedAtUtc: '2026-09-17T00:00:00Z', state: 'Draft', orderDate: null, revision: 0, calculation: zeroAdjustmentCalculation({ lines: [], incompleteLineCount: 1, merchandiseEstimate: null }) };
const ordered = { ...saved, state: 'Ordered', orderDate: '2026-09-16', revision: 1, version: 'v2' };
const receipt = { requestId: 'request', replayed: false, draftOrderId: saved.id, savedVersion: 'v2', completedAtUtc: saved.updatedAtUtc, revision: 1 };
beforeEach(() => { vi.clearAllMocks(); Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); vi.mocked(calculateDraft).mockResolvedValue(saved.calculation); });
const props = () => ({ id: saved.id, onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onCreated: vi.fn(), onCancel: vi.fn() });

it('offers invoice files directly on an ordered purchase without invoice accounting entry', async () => {
  // GIVEN an ordered purchase with its agreed contents.
  vi.mocked(getDraft).mockResolvedValue(ordered);
  render(<DraftEditor {...props()} />);
  await screen.findByRole('button', { name: 'Create amendment' });
  // WHEN reviewing the purchase THEN the owner can attach invoice files directly.
  expect(await screen.findByRole('button', { name: 'Add invoice files' })).toBeEnabled();
  expect(screen.queryByLabelText('Invoice number')).not.toBeInTheDocument();
});

it('keeps the ordered record in one view with contextual order and supplier details', async () => {
  // GIVEN saved order references, notes and supplier contacts alongside the agreed items.
  vi.mocked(getDraft).mockResolvedValue({ ...ordered, draft: { ...draft, supplierId: 'supplier-id', supplierEmail: 'orders@example.test', supplierOrderReference: 'SUP-123', platform: 'Direct', notes: 'Deliver together', sourceLinks: ['https://example.test/order', 'javascript:alert(1)'] } });
  render(<DraftEditor {...props()} />);
  await screen.findByRole('button', { name: 'Create amendment' });
  // THEN items occur once and there is no duplicated complete-record view.
  expect(screen.queryByText('Show complete agreed contents')).not.toBeInTheDocument();
  expect(screen.getAllByText('Sapphires')).toHaveLength(1);
  expect(screen.getByText('Deliver together')).not.toBeVisible();
  // WHEN opening contextual details THEN saved metadata is available without repeating the items.
  fireEvent.click(screen.getByLabelText('Order and supplier details'));
  expect(screen.getByText('Deliver together')).toBeVisible();
  expect(screen.getByText('orders@example.test')).toBeVisible();
  expect(screen.getByText('SUP-123')).toBeVisible();
  expect(screen.getByText('supplier-id')).toBeVisible();
  expect(screen.getByRole('link', { name: 'https://example.test/order' })).toHaveAttribute('rel', 'noopener noreferrer');
  expect(screen.queryByRole('link', { name: 'javascript:alert(1)' })).not.toBeInTheDocument();
  expect(screen.getAllByText('Sapphires')).toHaveLength(1);
});

it('requires saved content and explicit review before recording an order', async () => {
  // GIVEN a saved draft whose costs are unknown.
  vi.mocked(getDraft).mockResolvedValueOnce(saved).mockResolvedValue(ordered);
  vi.mocked(commitOrder).mockResolvedValue(receipt);
  render(<DraftEditor {...props()} />);
  const commit = await screen.findByRole('button', { name: 'Record as ordered' });
  // WHEN local content changes THEN commitment waits for a successful draft save.
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: 'Unsaved' } });
  expect(commit).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: draft.title } });
  fireEvent.click(commit);
  const review = screen.getByRole('dialog', { name: 'Record as ordered' });
  fireEvent.change(within(review).getByLabelText('Order date'), { target: { value: '2026-09-16' } });
  expect(commitOrder).not.toHaveBeenCalled();
  // WHEN the owner confirms THEN the saved version and explicit date are committed.
  fireEvent.click(within(review).getByRole('button', { name: 'Confirm order' }));
  await screen.findByRole('button', { name: 'Create amendment' });
  expect(commitOrder).toHaveBeenCalledWith(saved.id, expect.objectContaining({ expectedVersion: saved.version, orderDate: '2026-09-16' }));
  expect(screen.queryByRole('button', { name: 'Delete draft' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
  expect(screen.getByText('Supplier estimate')).toBeVisible();
});

it('retries an uncertain commitment with its original request and keeps the date frozen', async () => {
  // GIVEN a lost commitment response.
  vi.mocked(getDraft).mockResolvedValueOnce(saved).mockResolvedValue(ordered);
  vi.mocked(commitOrder).mockRejectedValueOnce(new TypeError('offline')).mockResolvedValue(receipt);
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Record as ordered' }));
  fireEvent.change(screen.getByLabelText('Order date'), { target: { value: '2026-09-16' } });
  fireEvent.click(screen.getByRole('button', { name: 'Confirm order' }));
  // WHEN retrying THEN it cannot create another commitment or change the submitted date.
  const retry = await screen.findByRole('button', { name: 'Check and retry commitment' });
  expect(screen.getByLabelText('Order date')).toBeDisabled();
  fireEvent.click(retry);
  await screen.findByRole('button', { name: 'Create amendment' });
  expect(vi.mocked(commitOrder).mock.calls[1]).toEqual(vi.mocked(commitOrder).mock.calls[0]);
});

it('focuses commitment validation and reveals saved-content fields without losing the entered date', async () => {
  // GIVEN a saved draft whose line needs correction according to the server.
  vi.mocked(getDraft).mockReset().mockResolvedValue(saved);
  vi.mocked(commitOrder).mockReset().mockRejectedValue(new DraftError(400, 'purchase_validation_failed', { 'draft.entries[0].description': ['Describe the ordered item.'], 'draft.entries[0].quantity': ['Enter an ordered quantity.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Record as ordered' }));
  // WHEN confirming without a date THEN keyboard focus identifies the affected input.
  const confirm = screen.getByRole('button', { name: 'Confirm order' }); confirm.focus(); fireEvent.click(confirm);
  expect(screen.getByLabelText('Order date')).toHaveFocus();
  expect(screen.getByLabelText('Order date')).toHaveAttribute('aria-invalid', 'true');
  fireEvent.change(screen.getByLabelText('Order date'), { target: { value: '2026-09-16' } });
  fireEvent.click(confirm);
  // WHEN following the authoritative saved-content error THEN its collapsed line opens for correction.
  fireEvent.click(await screen.findByRole('button', { name: 'Enter an ordered quantity.' }));
  await waitFor(() => expect(screen.getByLabelText('Quantity 1')).toHaveFocus());
  expect(screen.getByLabelText('Quantity 1').closest('details')).toHaveAttribute('open');
  // AND the review can be reopened with its entered date intact.
  fireEvent.click(screen.getByRole('button', { name: 'Record as ordered' }));
  expect(screen.getByLabelText('Order date')).toHaveValue('2026-09-16');
});

it('keeps an amendment on validation failure and requires review before submission', async () => {
  // GIVEN an ordered purchase and authoritative validation failure.
  vi.mocked(getDraft).mockResolvedValue(ordered);
  vi.mocked(calculateDraft).mockResolvedValue(saved.calculation);
  vi.mocked(amendOrder).mockRejectedValue(new DraftError(400, 'purchase_validation_failed', { reason: ['Explain this amendment.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create amendment' }));
  expect(screen.getByLabelText('Currency')).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: 'Corrected title' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Supplier correction' } });
  // WHEN reviewing THEN current and proposed content are inspectable before any write.
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  expect(amendOrder).not.toHaveBeenCalled();
  expect(screen.getByRole('region', { name: 'Changes' })).toHaveTextContent('Corrected title');
  fireEvent.click(screen.getByText('Show complete contents'));
  expect(screen.getByRole('heading', { name: 'Proposed contents' })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Record amendment' }));
  // THEN input survives rejection and the server's reason is shown.
  await waitFor(() => expect(screen.getByLabelText('Custom title (optional)')).toBeEnabled());
  expect(screen.getByLabelText('Custom title (optional)')).toHaveValue('Corrected title');
  expect(screen.getByLabelText('Amendment reason')).toHaveValue('Supplier correction');
  expect(screen.getAllByText('Explain this amendment.').length).toBeGreaterThan(0);
});

it('loads immutable revision contents on demand and retains them when another revision fails', async () => {
  // GIVEN an amended order and its original snapshot with unknown costs.
  vi.mocked(getDraft).mockResolvedValue({ ...ordered, revision: 2 });
  const first = { revision: 1, orderDate: ordered.orderDate, actorUserId: 'original-actor', recordedAtUtc: saved.createdAtUtc, reason: null, calculationPolicyVersion: 1, draft, calculation: saved.calculation, poReference: saved.poReference };
  vi.mocked(getOrderRevisions).mockResolvedValue({ items: [{ ...first, revision: 2, reason: 'Corrected quantity' }, first], nextCursor: null });
  vi.mocked(getOrderRevision).mockResolvedValueOnce(first).mockRejectedValueOnce(new TypeError('offline')).mockResolvedValueOnce(first);
  render(<DraftEditor {...props()} />);
  // WHEN inspecting the original commitment THEN its date, actor and unknown price are retained.
  fireEvent.click(await screen.findByRole('button', { name: 'View history' }));
  fireEvent.click(await screen.findByRole('button', { name: 'View revision 1' }));
  const detail = await screen.findByRole('region', { name: 'Revision 1 details' });
  const contents = within(detail).getByRole('region', { name: 'Agreed contents' });
  expect(within(contents).getByText('Sapphires')).toBeVisible();
  expect(within(contents).getByText('Unit price').nextElementSibling).toHaveTextContent('Unknown');
  // WHEN another revision fails THEN the selected evidence survives and retry is available.
  fireEvent.click(screen.getByRole('button', { name: 'View revision 2' }));
  await screen.findByRole('button', { name: 'Retry history' });
  expect(detail).toBeVisible();
});

it('does not resubmit a confirmed amendment when loading the new version fails', async () => {
  // GIVEN a successful write whose follow-up read fails.
  vi.mocked(getDraft).mockReset().mockResolvedValueOnce(ordered).mockRejectedValueOnce(new TypeError('offline')).mockResolvedValue({ ...ordered, revision: 2, version: 'v3' });
  vi.mocked(amendOrder).mockReset().mockResolvedValue({ ...receipt, revision: 2, savedVersion: 'v3' });
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create amendment' }));
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: 'Updated title' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Correct title' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  fireEvent.click(screen.getByRole('button', { name: 'Record amendment' }));
  // WHEN retrying recovery THEN only the read is repeated.
  fireEvent.click(await screen.findByRole('button', { name: 'Load current draft' }));
  await screen.findByRole('button', { name: 'Create amendment' });
  expect(amendOrder).toHaveBeenCalledTimes(1);
});

it('shows saved supplier and cost context before expanding commitment details', async () => {
  // GIVEN a saved purchase with an unknown line price.
  vi.mocked(getDraft).mockReset().mockResolvedValue(saved);
  render(<DraftEditor {...props()} />);
  // WHEN opening commitment THEN decision context is visible without opening the archived snapshot.
  fireEvent.click(await screen.findByRole('button', { name: 'Record as ordered' }));
  const summary = screen.getByRole('region', { name: 'Saved purchase summary' });
  expect(within(summary).getByText('Supplier')).toBeVisible();
  expect(within(summary).getByText('1 line · USD')).toBeVisible();
  expect(within(summary).getByText('Supplier estimate')).toBeVisible();
  expect(within(summary).getByText('Total purchase estimate')).toBeVisible();
  expect(within(summary).getByText(/Unknown or incomplete costs/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Confirm order' })).toBeEnabled();
});

it('reviews changes as a separate task and restores local edits when returning', async () => {
  // GIVEN an ordered purchase with a local title correction and an explanation.
  vi.mocked(getDraft).mockReset().mockResolvedValue(ordered);
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create amendment' }));
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: 'Corrected title' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Correct the title' } });
  // WHEN reviewing THEN only the verification task is present, with the edit form removed.
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  expect(screen.queryByLabelText('Title')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Add line' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Review amendment' })).not.toBeInTheDocument();
  expect(screen.getByRole('region', { name: 'Review amendment' })).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Review amendment' })).toHaveFocus();
  expect(amendOrder).not.toHaveBeenCalled();
  // WHEN returning to editing THEN both local changes and the reason remain intact.
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByLabelText('Custom title (optional)')).toHaveValue('Corrected title');
  expect(screen.getByLabelText('Amendment reason')).toHaveValue('Correct the title');
  expect(screen.getByRole('button', { name: 'Review amendment' })).toHaveFocus();
});

it('keeps a reviewed amendment frozen and retries the original request after a lost response', async () => {
  // GIVEN a reviewed amendment whose first response is lost.
  vi.mocked(getDraft).mockReset().mockResolvedValueOnce(ordered).mockResolvedValue({ ...ordered, revision: 2, version: 'v3' });
  vi.mocked(amendOrder).mockReset().mockRejectedValueOnce(new TypeError('offline')).mockResolvedValue({ ...receipt, revision: 2, savedVersion: 'v3' });
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create amendment' }));
  fireEvent.change(screen.getByLabelText('Custom title (optional)'), { target: { value: 'Confirmed stones' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Supplier confirmation' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  fireEvent.click(screen.getByRole('button', { name: 'Record amendment' }));
  // WHEN the response is uncertain THEN edits cannot change the submitted contents.
  const retry = await screen.findByRole('button', { name: 'Check and retry amendment' });
  expect(screen.getByRole('button', { name: 'Keep editing' })).toBeDisabled();
  expect(screen.queryByLabelText('Title')).not.toBeInTheDocument();
  expect(screen.getByRole('region', { name: 'Changes' })).toHaveTextContent('Confirmed stones');
  // WHEN retrying THEN the same idempotent request is reused and the saved record loads.
  fireEvent.click(retry);
  await screen.findByRole('button', { name: 'Create amendment' });
  expect(vi.mocked(amendOrder).mock.calls[1]).toEqual(vi.mocked(amendOrder).mock.calls[0]);
});

it.each([['Draft', saved, 'Record as ordered'], ['Ordered', ordered, 'Create amendment']] as const)('applies the same scroll transition to the %s toolbar', async (_state, purchase, action) => {
  // GIVEN a purchase at the top of its editing or ordered page.
  const observers = new Map<Element, IntersectionObserverCallback>();
  vi.stubGlobal('IntersectionObserver', class {
    private target?: Element;
    private callback: IntersectionObserverCallback;
    constructor(callback: IntersectionObserverCallback) { this.callback = callback; }
    observe(target: Element) { this.target = target; observers.set(target, this.callback); }
    disconnect() { if (this.target) observers.delete(this.target); }
  });
  try {
    vi.mocked(getDraft).mockReset().mockResolvedValue(purchase);
    const view = render(<DraftEditor {...props()} />);
    const toolbar = (await screen.findByRole('button', { name: action })).closest('.po-editor-toolbar');
    // AND the displayed toolbar's marker is observed after any loading-view replacement.
    const marker = toolbar!.previousElementSibling!;
    await waitFor(() => expect(observers.has(marker)).toBe(true));
    const reportIntersection = observers.get(marker)!;
    expect(toolbar).not.toHaveClass('is-pinned');
    // WHEN its top marker scrolls above the viewport THEN the shared toolbar gains its opaque treatment.
    await act(async () => reportIntersection([{ target: marker, isIntersecting: false, boundingClientRect: { top: -10 } } as IntersectionObserverEntry], {} as IntersectionObserver));
    expect(toolbar).toHaveClass('is-pinned');
    // WHEN returning to the top THEN it becomes transparent again.
    await act(async () => reportIntersection([{ target: marker, isIntersecting: true, boundingClientRect: { top: 10 } } as IntersectionObserverEntry], {} as IntersectionObserver));
    expect(toolbar).not.toHaveClass('is-pinned');
    // AND leaving the page releases the scroll observer.
    view.unmount();
    expect(observers.size).toBe(0);
  } finally { vi.unstubAllGlobals(); }
});
