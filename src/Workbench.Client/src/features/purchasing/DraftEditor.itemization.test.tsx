import { zeroAdjustmentCalculation } from '../../test/draftCalculationFixture';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { DraftEditor } from './DraftEditor';
import { createDraft, getDraft, calculateDraft, DraftError } from '../../api/purchaseOrders';
vi.mock('../../api/purchaseOrders', async original => ({ ...await original<typeof import('../../api/purchaseOrders')>(), createDraft: vi.fn(), getDraft: vi.fn(), calculateDraft: vi.fn() }));
beforeEach(() => { vi.mocked(createDraft).mockReset(); vi.mocked(getDraft).mockReset(); vi.mocked(calculateDraft).mockResolvedValue(zeroAdjustmentCalculation({ lines: [], incompleteLineCount: 1, merchandiseEstimate: null })); Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } }); });
const props = () => ({ onDirtyChange: vi.fn(), onAuthLost: vi.fn(), onSaved: vi.fn(), onCancel: vi.fn(), onCreated: vi.fn() });
it('keeps the lower Add line action before the merchandise estimate', () => {
  // GIVEN a draft editor with an item to price.
  const { container } = render(<DraftEditor {...props()} />);
  // WHEN adding a line THEN the next-line action precedes the estimate in reading and tab order.
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  const addLine = screen.getAllByRole('button', { name: 'Add line' }).at(-1)!;
  const estimate = container.querySelector('.po-merchandise-estimate')!;
  expect(estimate).toBeInTheDocument();
  expect(addLine.compareDocumentPosition(estimate) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
});

it('records supplier quantities once and saves either unit or total line pricing', async () => {
  // GIVEN a supplier quote per carat.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  fireEvent.change(screen.getByLabelText('Description 1'), { target: { value: 'Sapphires' } });
  fireEvent.change(screen.getByLabelText('Quantity 1'), { target: { value: '12.5' } });
  fireEvent.change(screen.getByLabelText('Unit 1'), { target: { value: 'carat' } });
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'USD' } });
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '2000' } });
  // THEN there is one quantity and unit, with no separate pricing basis.
  expect(screen.getByRole('radio', { name: 'Per unit 1' })).toBeChecked();
  for (const name of ['Pricing unit 1', 'Per quantity 1', 'Total quantity priced 1']) expect(screen.queryByLabelText(name)).not.toBeInTheDocument();
  // WHEN saving THEN the supplier basis is explicit.
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await waitFor(() => expect(createDraft).toHaveBeenCalled());
  expect(vi.mocked(createDraft).mock.calls.at(-1)![0].draft.entries[0]).toMatchObject({ quantity: '12.5', unitOfMeasure: 'carat', priceMode: 'perUnit', price: '20.00', indicativePrice: null, legacyPricing: null });
});

it('allows total line pricing without quantity and retains the amount when switching modes', async () => {
  // GIVEN a fixed supplier quote whose quantity and unit are unknown.
  vi.mocked(createDraft).mockRejectedValue(new DraftError(400));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  fireEvent.change(screen.getByLabelText('Currency'), { target: { value: 'USD' } });
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '25000' } });
  // WHEN changing the interpretation THEN the amount is retained and clearly relabelled.
  fireEvent.click(screen.getByRole('radio', { name: 'Total line 1' }));
  expect(screen.getByLabelText('Total line price 1')).toHaveValue('250.00');
  expect(screen.queryByLabelText('Unit price 1')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('');
  fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  // THEN a fixed quote can be saved without invented units.
  await waitFor(() => expect(createDraft).toHaveBeenCalled());
  expect(vi.mocked(createDraft).mock.calls.at(-1)![0].draft.entries[0]).toMatchObject({ quantity: null, unitOfMeasure: null, priceMode: 'lineTotal', price: '250.00' });
  fireEvent.click(screen.getByRole('radio', { name: 'Per unit 1' }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('250.00');
});

it.each(['perUnit', 'lineTotal'])('preserves a legacy reference until the owner explicitly adopts %s pricing', async mode => {
  // GIVEN an older saved line with a basisless reference price.
  const content = { orderDiscount: null, charges: [], title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [{ discount: null, id: 'line', description: 'Parcel', indicativePrice: '300.0000', notes: null, sourceLink: null, quantity: null, unitOfMeasure: null, priceMode: 'perUnit', price: null, legacyPricing: null, supplierSku: 'P-31', itemType: 'Gemstone' }] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'draft', draft: content, version: 'version', createdAtUtc: '2026-09-16', updatedAtUtc: '2026-09-16', supplierIsArchived: false, poReference: 'PO-000001', calculation: zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: null }], incompleteLineCount: 1, merchandiseEstimate: null }) });
  render(<DraftEditor {...props()} id="draft" />);
  // THEN the reference is visible without invented quantity or unit price.
  fireEvent.click(await screen.findByLabelText(/^Edit line 1:/));
  await screen.findByText('Reference price — basis not recorded');
  fireEvent.click(screen.getByText('Line details'));
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('');
  expect(screen.queryByLabelText('Unit price 1')).not.toBeInTheDocument();
  expect(screen.getByLabelText('Supplier SKU 1')).toBeVisible();
  // WHEN the owner chooses the meaning THEN conversion remains local and incomplete.
  fireEvent.click(screen.getByRole('button', { name: mode === 'perUnit' ? 'Use as unit price' : 'Use as total line price' }));
  expect(screen.getByLabelText(mode === 'perUnit' ? 'Unit price 1' : 'Total line price 1')).toHaveValue('300.00');
  await waitFor(() => expect(vi.mocked(calculateDraft).mock.calls.at(-1)![0].entries[0]).toMatchObject({ priceMode: mode, price: '300.00', indicativePrice: null }));
  expect(screen.getByLabelText('Unit 1')).toHaveValue('');
  expect(screen.queryByLabelText('Reference price 1')).not.toBeInTheDocument();
  expect(createDraft).not.toHaveBeenCalled();
});
it('clears prices after confirmation while preserving quantities, basis and optional details', async () => {
  // GIVEN a locally itemized draft with recoverable details.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  fireEvent.change(screen.getByLabelText('Quantity 1'), { target: { value: '250' } });
  fireEvent.change(screen.getByLabelText('Unit 1'), { target: { value: 'piece' } });
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '800' } });
  fireEvent.change(screen.getByLabelText('Supplier SKU 1'), { target: { value: 'SET-9' } });
  // WHEN clearing is cancelled THEN the price stays; confirmation clears only the amount.
  fireEvent.click(screen.getByRole('button', { name: 'Clear all amounts' }));
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('8.00');
  fireEvent.click(screen.getByRole('button', { name: 'Clear all amounts' }));
  fireEvent.click(screen.getByRole('button', { name: /^Clear amounts$/ }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('');
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('250');
  expect(screen.getByLabelText('Unit 1')).toHaveValue('piece');
  expect(screen.getByLabelText('Supplier SKU 1')).toHaveValue('SET-9');
});
it('shows authoritative preview errors on order fields and in the linked summary', async () => {
  // GIVEN a unit price without its required order currency.
  vi.mocked(calculateDraft).mockRejectedValue(new DraftError(400, 'draft_validation_failed', { 'draft.currency': ['Choose a currency when entering a price.'] }));
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  fireEvent.change(screen.getByLabelText('Unit price 1'), { target: { value: '100' } });
  // WHEN preview validation returns THEN its exact field error is actionable without first saving.
  const link = await screen.findByRole('link', { name: 'Choose a currency when entering a price.' });
  expect(screen.getByLabelText('Currency')).toHaveAttribute('aria-invalid', 'true');
  fireEvent.click(link);
  expect(screen.getByLabelText('Currency')).toHaveFocus();
  expect(createDraft).not.toHaveBeenCalled();
});

it('combines native unit selectors with their accessible labels', () => {
  // GIVEN an empty purchase-order line.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  // WHEN choosing units THEN both selectors use the shared combined label control.
  for (const name of ['Description 1', 'Quantity 1', 'Unit 1', 'Unit price 1']) {
    const control = screen.getByLabelText(name);
    expect(control.closest('.floating-field')).not.toBeNull();
    expect(control).toHaveAccessibleName(name);
    // AND the visible label omits redundant numbering while the accessible name keeps context.
    expect(control.closest('.floating-field')?.querySelector('label')).toHaveTextContent(new RegExp('^' + name.replace(/ 1$/, '') + '$'));
  }
});

it('keeps quantity inputs blank after a unit is chosen', () => {
  // GIVEN a new empty purchase line.
  render(<DraftEditor {...props()} />);
  fireEvent.click(screen.getAllByRole('button', { name: 'Add line' })[0]);
  // WHEN its ordered unit is chosen THEN no quantity or pricing basis is invented.
  fireEvent.change(screen.getByLabelText('Unit 1'), { target: { value: 'piece' } });
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('');
  expect(screen.queryByLabelText('Per quantity 1')).not.toBeInTheDocument();
  expect(screen.queryByLabelText('Pricing unit 1')).not.toBeInTheDocument();
});

it('opens saved lines as readable summaries and keeps save state beside its action', async () => {
  // GIVEN a saved order with populated optional metadata.
  const entry = { discount: null, id: 'line', description: 'Blue sapphires', indicativePrice: null, notes: null, sourceLink: null, quantity: '12.5000', unitOfMeasure: 'carat', priceMode: 'perUnit', price: '20.0000', legacyPricing: null, supplierSku: 'SAP-10', itemType: 'Gemstone' };
  const content = { orderDiscount: null, charges: [], title: 'Sample', supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [entry] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'draft', draft: content, version: 'v1', createdAtUtc: '2026-09-16', updatedAtUtc: '2026-09-16', supplierIsArchived: false, poReference: 'PO-000001', calculation: zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: '250.0000' }], incompleteLineCount: 0, merchandiseEstimate: '250.0000' }) });
  render(<DraftEditor {...props()} id="draft" />);
  // THEN the item identity is visible and its editing fields remain folded.
  const summary = await screen.findByLabelText(/^Edit line 1:/);
  expect(summary).toHaveTextContent('Blue sapphires');
  expect(summary).toHaveAccessibleName('Edit line 1: Blue sapphires');
  expect(summary).toHaveTextContent('12.5 carats');
  expect(screen.getByLabelText('Quantity 1')).not.toBeVisible();
  // WHEN editing THEN quantities drop insignificant zeros, prices retain two decimals, and metadata stays folded.
  fireEvent.click(summary);
  expect(screen.getByLabelText('Quantity 1')).toBeVisible();
  expect(screen.getByLabelText('Quantity 1')).toHaveValue('12.5');
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('20.00');
  expect(screen.getByLabelText('Supplier SKU 1')).not.toBeVisible();
  fireEvent.change(screen.getByLabelText('Quantity 1'), { target: { value: '10.125' } });
  expect(screen.getByText('Unsaved changes').closest('.po-editor-toolbar')).not.toBeNull();
});

it('keeps ambiguous previous pricing read-only until explicit replacement', async () => {
  // GIVEN an older quote whose original basis cannot be converted without guessing.
  const legacyPricing = { quantity: '10.0000', unitOfMeasure: 'piece', unitPrice: '20.0000', pricingUnit: 'carat', pricePerQuantity: '1.0000', pricingQuantity: null };
  const entry = { discount: null, id: 'line', description: 'Parcel', notes: null, sourceLink: null, quantity: null, unitOfMeasure: null, priceMode: 'perUnit', price: null, indicativePrice: null, legacyPricing, supplierSku: null, itemType: null };
  const content = { orderDiscount: null, charges: [], title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [entry] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'draft', draft: content, version: 'v1', createdAtUtc: '2026-09-16', updatedAtUtc: '2026-09-16', supplierIsArchived: false, poReference: 'PO-000001', calculation: zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: null }], incompleteLineCount: 1, merchandiseEstimate: null }) });
  render(<DraftEditor {...props()} id="draft" />);
  fireEvent.click(await screen.findByLabelText(/^Edit line 1:/));
  // THEN no editable price invents a new interpretation.
  expect(screen.queryByLabelText('Unit price 1')).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Replace previous pricing' })).toBeVisible();
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
  // WHEN explicitly replacing THEN the owner can enter a new supplier basis without a guessed price.
  fireEvent.click(screen.getByRole('button', { name: 'Replace previous pricing' }));
  expect(screen.getByLabelText('Unit price 1')).toHaveValue('');
  expect(screen.queryByRole('button', { name: 'Replace previous pricing' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled();
  expect(createDraft).not.toHaveBeenCalled();
});

it.each(['perUnit', 'lineTotal'])('shows reference and incomplete previous pricing together and replaces both on %s adoption', async mode => {
  // GIVEN a valid older reference quote with additional incomplete pricing metadata.
  const legacyPricing = { quantity: '10.0000', unitOfMeasure: 'piece', unitPrice: null, pricingUnit: 'carat', pricePerQuantity: '1.0000', pricingQuantity: null };
  const entry = { discount: null, id: 'line', description: 'Parcel', notes: null, sourceLink: null, quantity: null, unitOfMeasure: null, priceMode: 'perUnit', price: null, indicativePrice: '300.0000', legacyPricing, supplierSku: null, itemType: null };
  const content = { orderDiscount: null, charges: [], title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [entry] };
  vi.mocked(getDraft).mockResolvedValue({ id: 'draft', draft: content, version: 'v1', createdAtUtc: '2026-09-16', updatedAtUtc: '2026-09-16', supplierIsArchived: false, poReference: 'PO-000001', calculation: zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: null }], incompleteLineCount: 1, merchandiseEstimate: null }) });
  render(<DraftEditor {...props()} id="draft" />);
  fireEvent.click(await screen.findByLabelText(/^Edit line 1:/));
  // THEN both preserved meanings are visible without an editable or guessed price.
  expect(screen.getByText('Reference price \u2014 basis not recorded')).toBeVisible();
  expect(screen.getByText('Previous pricing needs review')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Replace previous pricing' })).toBeVisible();
  expect(screen.queryByLabelText('Unit price 1')).not.toBeInTheDocument();
  // WHEN the owner explicitly adopts the reference THEN both legacy representations are replaced.
  fireEvent.click(screen.getByRole('button', { name: mode === 'perUnit' ? 'Use as unit price' : 'Use as total line price' }));
  expect(screen.getByLabelText(mode === 'perUnit' ? 'Unit price 1' : 'Total line price 1')).toHaveValue('300.00');
  await waitFor(() => expect(vi.mocked(calculateDraft).mock.calls.at(-1)![0].entries[0]).toMatchObject({ priceMode: mode, price: '300.00', indicativePrice: null, legacyPricing: null }));
  expect(screen.queryByText('Previous pricing needs review')).not.toBeInTheDocument();
  expect(createDraft).not.toHaveBeenCalled();
});
