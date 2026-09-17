import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import { DraftEditor } from './DraftEditor';
import { calculateDraft, createDraft, DraftError, getDraft, type DraftContent } from '../../api/purchaseOrders';
import { zeroAdjustmentCalculation } from '../../test/draftCalculationFixture';
vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), calculateDraft: vi.fn(), createDraft: vi.fn(), getDraft: vi.fn() }));
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onCreated: vi.fn(), onCancel: vi.fn() });
beforeEach(() => {
  vi.mocked(createDraft).mockReset().mockRejectedValue(new DraftError(400));
  vi.mocked(getDraft).mockReset();
  vi.mocked(calculateDraft).mockReset().mockResolvedValue(zeroAdjustmentCalculation({ lines: [], incompleteLineCount: 0, merchandiseEstimate: null }));
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
});

it('saves line and order discounts with independently categorized third-party charges', async () => {
  // GIVEN a draft with merchandise and a currency.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'USD' } });
  // WHEN the owner adds discounts and a tariff collected by a carrier.
  fireEvent.click(screen.getByRole('button', { name: 'Add line discount 1' }));
  fireEvent.change(screen.getByLabelText('Line discount 1 type'), { target: { value: 'percentage' } });
  fireEvent.change(screen.getByLabelText('Line discount 1 percentage'), { target: { value: '10' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add order discount' }));
  fireEvent.change(screen.getByLabelText('Order discount amount'), { target: { value: '1000' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add charge' }));
  fireEvent.change(screen.getByLabelText('Category 1'), { target: { value: 'customsDuty' } });
  fireEvent.change(screen.getByLabelText('Charge amount 1'), { target: { value: '300' } });
  fireEvent.change(screen.getByLabelText('Payee 1'), { target: { value: 'thirdParty' } });
  fireEvent.change(screen.getByLabelText('Payee name 1'), { target: { value: 'Carrier' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN each adjustment has its own scope, basis and payee in the saved request.
  await waitFor(() => expect(createDraft).toHaveBeenCalled());
  const draft = vi.mocked(createDraft).mock.calls[0][0].draft;
  expect(draft.entries[0]).toMatchObject({ discount: { mode: 'percentage', value: '10' } });
  expect(draft).toMatchObject({ orderDiscount: { mode: 'fixed', value: '10.00' }, charges: [{ category: 'customsDuty', label: 'Customs duty / tariff', amount: '3.00', payeeKind: 'thirdParty', payeeName: 'Carrier', amountStatus: 'estimated' }] });
});

it('keeps charge-only drafts calculable and restores a removed charge before save', async () => {
  // GIVEN a purchase with no merchandise entered yet.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add charge' }));
  fireEvent.change(screen.getByLabelText('Charge label 1'), { target: { value: 'Import VAT' } });
  // WHEN a charge is removed and restored THEN input and identity survive.
  await waitFor(() => expect(calculateDraft).toHaveBeenCalled());
  const first = vi.mocked(calculateDraft).mock.calls.at(-1)![0];
  fireEvent.click(screen.getByRole('button', { name: 'Remove charge 1' }));
  fireEvent.click(screen.getByRole('button', { name: 'Undo charge removal' }));
  expect(screen.getByLabelText('Charge label 1')).toHaveValue('Import VAT');
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await waitFor(() => expect(createDraft).toHaveBeenCalled());
  expect(vi.mocked(createDraft).mock.calls[0][0].draft.charges[0].id).toBe(first.charges[0].id);
});

it('clears all amounts only after confirmation and retains charge descriptions', () => {
  // GIVEN a charge with an entered amount and optional order discount.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add charge' }));
  fireEvent.change(screen.getByLabelText('Charge amount 1'), { target: { value: '2500' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add order discount' }));
  // WHEN cancelling the clear action THEN the amount remains.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all amounts' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }));
  expect(screen.getByLabelText('Charge amount 1')).toHaveValue('25.00');
  // WHEN confirming THEN amounts and discounts clear, but the charge stays.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all amounts' }));
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Clear amounts' }));
  expect(screen.getByLabelText('Charge amount 1')).toHaveValue('');
  expect(screen.getByLabelText('Charge label 1')).toHaveValue('Shipping / freight');
  expect(screen.getByRole('button', { name: 'Add order discount' })).toBeVisible();
});

it('locks saved monetary currency and requires a reason before clearing confirmed charges', async () => {
  // GIVEN a saved confirmed third-party charge without merchandise prices.
  const draft: DraftContent = { title: 'Freight', supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [], orderDiscount: { mode: 'fixed', value: '10.0000' }, charges: [{ id: 'charge', category: 'shipping', label: 'Freight', amount: '15.0000', payeeKind: 'thirdParty', payeeName: 'Carrier', amountStatus: 'confirmed', reference: 'Quote 1', notes: 'Original quote.' }] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'saved', draft, version: 'version', poReference: 'PO-000001', supplierIsArchived: false, createdAtUtc: '2026-09-17', updatedAtUtc: '2026-09-17', calculation: zeroAdjustmentCalculation({ lines: [], incompleteLineCount: 0, merchandiseEstimate: null }) });
  render(<DraftEditor {...props()} id="saved" />);
  await screen.findByLabelText('Charge label 1');
  expect(screen.getByLabelText('Order discount amount')).toHaveValue('10.00');
  expect(screen.getByLabelText('Currency')).toBeDisabled();
  // WHEN clearing confirmed amounts THEN an explicit correction reason is required.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all amounts' }));
  const dialog = within(screen.getByRole('dialog'));
  expect(dialog.getByRole('button', { name: 'Clear amounts' })).toBeDisabled();
  fireEvent.change(dialog.getByLabelText(/Reason for clearing/), { target: { value: 'Re-entering in the quoted currency.' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Clear amounts' }));
  // THEN metadata and the original explanation remain; currency stays locked until save.
  expect(screen.getByLabelText('Charge notes 1')).toHaveValue('Original quote.\nRe-entering in the quoted currency.');
  expect(screen.getByLabelText('Amount status 1')).toHaveValue('estimated');
  expect(screen.getByLabelText('Currency')).toBeDisabled();
});

it('retains edits and reveals the charge field when preview validation fails', async () => {
  // GIVEN a source charge with a missing third-party payee name.
  vi.mocked(calculateDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.charges[0].payeeName': ['Name the third-party payee.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add charge' }));
  fireEvent.change(screen.getByLabelText('Payee 1'), { target: { value: 'thirdParty' } });
  fireEvent.change(screen.getByLabelText('Charge label 1'), { target: { value: 'Import VAT' } });
  // WHEN the error summary is followed THEN the field receives focus and local work is kept.
  fireEvent.click(await screen.findByRole('link', { name: 'Name the third-party payee.' }));
  expect(screen.getByLabelText('Payee name 1')).toHaveFocus();
  expect(screen.getByLabelText('Payee name 1')).toHaveAttribute('aria-invalid', 'true');
  expect(screen.getByLabelText('Charge label 1')).toHaveValue('Import VAT');
  expect(createDraft).not.toHaveBeenCalled();
});
