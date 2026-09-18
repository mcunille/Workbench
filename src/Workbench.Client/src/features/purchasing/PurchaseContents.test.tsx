import { render, screen, within } from '@testing-library/react';
import type { DraftContent } from '../../api/purchaseOrders';
import { zeroAdjustmentCalculation } from '../../test/draftCalculationFixture';
import { emptyLine } from './draftLine';
import { PurchaseContents } from './PurchaseContents';

const draft: DraftContent = {
  title: 'September stones', supplierName: 'Stone supplier', supplierId: null,
  supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null,
  supplierPostalAddress: null, supplierOrderReference: null, platform: null,
  currency: 'USD', notes: null, sourceLinks: [], orderDiscount: null, charges: [],
  entries: [{ ...emptyLine('sapphire'), description: 'Blue sapphire', quantity: '2.0000', unitOfMeasure: 'carat', price: '12.3400' }],
};

it('leads with merchandise and uses the exact server estimate without empty metadata', () => {
  // GIVEN a priced line whose authoritative net differs from quantity times price.
  const calculation = zeroAdjustmentCalculation({ lines: [{ id: 'sapphire', gross: '24.6800' }], incompleteLineCount: 0, merchandiseEstimate: '24.6800' });
  calculation.lines[0].net = '23.4567';
  // WHEN inspecting the compact agreed contents.
  render(<PurchaseContents draft={draft} calculation={calculation} />);
  // THEN the item, quantity, pricing basis and exact estimate are readable together.
  const line = screen.getByRole('listitem');
  expect(within(line).getByText('Blue sapphire')).toBeVisible();
  expect(within(line).getByText('2 carats')).toBeVisible();
  expect(within(line).getByText('Unit price').nextElementSibling).toHaveTextContent('USD 12.34');
  expect(within(line).getByText('Line estimate').nextElementSibling).toHaveTextContent('USD 23.4567');
  // AND optional empty snapshot fields do not bury the items.
  expect(screen.queryByText('Supplier directory link')).not.toBeInTheDocument();
  expect(screen.queryByText('Not set')).not.toBeInTheDocument();
  expect(screen.queryByText('None')).not.toBeInTheDocument();
});

it('distinguishes zero and unknown prices and preserves charge payees and status', () => {
  // GIVEN free merchandise, an unpriced line, and supplier and third-party charges.
  const content = { ...draft, entries: [
    { ...draft.entries[0], priceMode: 'lineTotal', price: '0.0000' },
    { ...emptyLine('unknown'), description: 'Unpriced parcel' },
  ], charges: [
    { id: 'shipping', category: 'shipping', label: 'Shipping', amount: '0.0000', payeeKind: 'supplier', payeeName: null, amountStatus: 'confirmed', reference: null, notes: null },
    { id: 'duty', category: 'customsDuty', label: 'Customs duty', amount: null, payeeKind: 'thirdParty', payeeName: 'Customs broker', amountStatus: 'estimated', reference: null, notes: null },
  ] };
  // WHEN reading compact contents without a calculation.
  render(<PurchaseContents draft={content} heading="Saved contents" />);
  // THEN line totals stay distinct from unit prices and missing values remain unknown.
  expect(screen.getByText('Total line price').nextElementSibling).toHaveTextContent('USD 0.00');
  expect(screen.getByText('Unit price').nextElementSibling).toHaveTextContent('Unknown');
  expect(screen.getByText('Quantity not set')).toBeVisible();
  expect(screen.queryByText('Line estimate')).not.toBeInTheDocument();
  // AND each charge preserves its payee, amount and confirmation meaning.
  const shipping = screen.getByText('Shipping').closest('li')!;
  expect(shipping).toHaveTextContent('Supplier: Stone supplier');
  expect(shipping).toHaveTextContent('Confirmed from source');
  expect(shipping).toHaveTextContent('USD 0.00');
  const duty = screen.getByText('Customs duty').closest('li')!;
  expect(duty).toHaveTextContent('Third party: Customs broker');
  expect(duty).toHaveTextContent('Estimated');
  expect(duty).toHaveTextContent('Unknown');
});

it('keeps discounts and incomplete estimates explicit', () => {
  // GIVEN percentage and fixed discounts and an incomplete server estimate.
  const content = { ...draft, orderDiscount: { mode: 'fixed', value: '1.2500' }, entries: [{ ...draft.entries[0], discount: { mode: 'percentage', value: '5.0000' } }] };
  const calculation = zeroAdjustmentCalculation({ lines: [{ id: 'sapphire', gross: null }], incompleteLineCount: 1, merchandiseEstimate: null });
  // WHEN reading the agreed contents.
  render(<PurchaseContents draft={content} calculation={calculation} />);
  // THEN discounts remain visible and incomplete calculation is never rendered as zero.
  expect(screen.getByText('Line discount').nextElementSibling).toHaveTextContent('5%');
  expect(screen.getByText('Order discount: USD 1.25 on merchandise after line discounts')).toBeVisible();
  expect(screen.getByText('Line estimate').nextElementSibling).toHaveTextContent('Unknown');
});
