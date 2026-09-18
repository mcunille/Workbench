import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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

it('requires saved content and explicit review before recording an order', async () => {
  // GIVEN a saved draft whose costs are unknown.
  vi.mocked(getDraft).mockResolvedValueOnce(saved).mockResolvedValue(ordered);
  vi.mocked(commitOrder).mockResolvedValue(receipt);
  render(<DraftEditor {...props()} />);
  const commit = await screen.findByRole('button', { name: 'Record as ordered' });
  // WHEN local content changes THEN commitment waits for a successful draft save.
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Unsaved' } });
  expect(commit).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: draft.title } });
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

it('keeps an amendment on validation failure and requires review before submission', async () => {
  // GIVEN an ordered purchase and authoritative validation failure.
  vi.mocked(getDraft).mockResolvedValue(ordered);
  vi.mocked(calculateDraft).mockResolvedValue(saved.calculation);
  vi.mocked(amendOrder).mockRejectedValue(new DraftError(400, 'purchase_validation_failed', { reason: ['Explain this amendment.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Create amendment' }));
  expect(screen.getByLabelText('Currency')).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Corrected title' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Supplier correction' } });
  // WHEN reviewing THEN current and proposed content are inspectable before any write.
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  expect(amendOrder).not.toHaveBeenCalled();
  expect(screen.getByRole('heading', { name: 'Proposed contents' })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Record amendment' }));
  // THEN input survives rejection and the server's reason is shown.
  await waitFor(() => expect(screen.getByLabelText('Title')).toBeEnabled());
  expect(screen.getByLabelText('Title')).toHaveValue('Corrected title');
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
  expect(within(detail).getByText('Sapphires')).toBeVisible();
  expect(within(detail).getByText('Unit price').nextElementSibling).toHaveTextContent('Unknown');
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
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Updated title' } });
  fireEvent.change(screen.getByLabelText('Amendment reason'), { target: { value: 'Correct title' } });
  fireEvent.click(screen.getByRole('button', { name: 'Review amendment' }));
  fireEvent.click(screen.getByRole('button', { name: 'Record amendment' }));
  // WHEN retrying recovery THEN only the read is repeated.
  fireEvent.click(await screen.findByRole('button', { name: 'Load current draft' }));
  await screen.findByRole('button', { name: 'Create amendment' });
  expect(amendOrder).toHaveBeenCalledTimes(1);
});
