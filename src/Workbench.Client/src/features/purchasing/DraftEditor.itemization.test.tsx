import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { DraftEditor } from './DraftEditor';
import { createDraft, getDraft, calculateDraft, DraftError } from '../../api/purchaseOrders';
vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), createDraft: vi.fn(), getDraft: vi.fn(), calculateDraft: vi.fn() }));
beforeEach(() => { vi.mocked(createDraft).mockReset(); vi.mocked(getDraft).mockReset(); vi.mocked(calculateDraft).mockResolvedValue({ lines: [], incompleteLineCount: 1, merchandiseEstimate: null }); Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); });
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onCancel: vi.fn(), onCreated: vi.fn() });
it('keeps ordered pieces separate from the total carat weight used for pricing', async () => {
  // GIVEN an owner entering ten stones priced by weight.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add entry' }));
  fireEvent.change(screen.getByLabelText('Description 1'), { target: { value: 'Ten sapphires' } });
  fireEvent.change(screen.getByLabelText('Quantity 1'), { target: { value: '10' } });
  fireEvent.change(screen.getByLabelText('Unit 1'), { target: { value: 'piece' } });
  // THEN the initial pricing basis is visibly one piece.
  expect(screen.getByLabelText('Pricing unit 1')).toHaveValue('piece');
  expect(screen.getByLabelText('Per quantity 1')).toHaveValue('1');
  // WHEN the owner states a different pricing unit and its total quantity.
  fireEvent.change(screen.getByLabelText('Pricing unit 1'), { target: { value: 'carat' } });
  fireEvent.change(screen.getByLabelText('Total quantity priced 1'), { target: { value: '12.5' } });
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'USD' } });
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '2000' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN both meanings survive the save boundary without conversion.
  await waitFor(() => expect(createDraft).toHaveBeenCalled());
  expect(vi.mocked(createDraft).mock.calls.at(-1)![0].draft.entries[0]).toMatchObject({ quantity: '10', unitOfMeasure: 'piece', pricingUnit: 'carat', pricingQuantity: '12.5', pricePerQuantity: '1', unitPrice: '20.00', indicativePrice: null });
});

it('preserves a legacy reference amount until the owner explicitly supplies its meaning', async () => {
  // GIVEN an older saved line with a basisless reference price.
  const content = { title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [{ id: 'line', description: 'Parcel', indicativePrice: '300.0000', notes: null, sourceLink: null, quantity: null, unitOfMeasure: null, unitPrice: null, pricingUnit: null, pricePerQuantity: null, pricingQuantity: null, supplierSku: 'P-31', itemType: 'Gemstone' }] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'draft', draft: content, version: 'version', createdAtUtc: '2026-09-16', updatedAtUtc: '2026-09-16', supplierIsArchived: false, poReference: 'PO-000001', calculation: { lines: [{ id: 'line', gross: null }], incompleteLineCount: 1, merchandiseEstimate: null } });
  render(<DraftEditor {...props()} id="draft" />);
  // THEN the reference is visible without invented quantity or unit price.
  await screen.findByText('Reference price — basis not recorded');
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('');
  expect(screen.queryByLabelText('Unit price 1')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Supplier SKU 1')).toBeVisible();
  // WHEN the owner chooses the meaning THEN conversion remains local and incomplete.
  fireEvent.click(screen.getByRole('button', { name: 'Use as unit price' }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('300.00');
  expect(screen.getByLabelText('Pricing unit 1')).toHaveValue('');
  expect(screen.queryByLabelText('Reference price 1')).not.toBeInTheDocument();
  expect(createDraft).not.toHaveBeenCalled();
});
it('clears prices after confirmation while preserving quantities, basis and optional details', async () => {
  // GIVEN a locally itemized draft with recoverable details.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add entry' }));
  fireEvent.change(screen.getByLabelText('Quantity 1'), { target: { value: '250' } });
  fireEvent.change(screen.getByLabelText('Unit 1'), { target: { value: 'piece' } });
  fireEvent.change(screen.getByLabelText('Per quantity 1'), { target: { value: '100' } });
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '800' } });
  fireEvent.change(screen.getByLabelText('Supplier SKU 1'), { target: { value: 'SET-9' } });
  // WHEN clearing is cancelled THEN the price stays; confirmation clears only the amount.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all prices' }));
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('8.00');
  fireEvent.click(screen.getByRole('button', { name: 'Clear all prices' }));
  fireEvent.click(screen.getByRole('button', { name: /^Clear prices$/ }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('');
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('250');
  expect(screen.getByLabelText('Per quantity 1')).toHaveValue('100');
  expect(screen.getByLabelText('Supplier SKU 1')).toHaveValue('SET-9');
});
