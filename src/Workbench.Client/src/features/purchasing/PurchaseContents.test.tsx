import { fireEvent, render, screen, within } from '@testing-library/react';
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
  expect(screen.queryByLabelText('Details for line 1')).not.toBeInTheDocument();
});

it('reveals supplemental line details independently without repeating the primary facts', () => {
  // GIVEN two lines with supplemental information, including a zero reference price and legacy quote.
  const content = { ...draft, entries: [
    { ...draft.entries[0], supplierSku: 'S-123', itemType: 'Gemstone', indicativePrice: '0.0000', notes: 'Inspect inclusions', sourceLink: 'https://example.com/stone', legacyPricing: { quantity: '2', unitOfMeasure: 'carat', unitPrice: '10', pricingUnit: 'carat', pricePerQuantity: '1', pricingQuantity: '2' } },
    { ...emptyLine('second'), description: 'Ruby', notes: 'Second line notes' },
  ] };
  render(<PurchaseContents draft={content} />);
  const first = screen.getByLabelText('Details for line 1');
  const second = screen.getByLabelText('Details for line 2');
  expect(screen.getByText('S-123')).not.toBeVisible();
  // WHEN opening the first line THEN its metadata is available without opening other lines.
  fireEvent.click(first);
  expect(first.closest('details')).toHaveAttribute('open');
  expect(second.closest('details')).not.toHaveAttribute('open');
  expect(screen.getByText('S-123')).toBeVisible();
  expect(screen.getByText('Gemstone')).toBeVisible();
  expect(screen.getByText('Reference price').nextElementSibling).toHaveTextContent('USD 0.00');
  expect(screen.getByText('Inspect inclusions')).toBeVisible();
  expect(screen.getByText('Quoted price').nextElementSibling).toHaveTextContent('10.00 USD');
  expect(screen.getByRole('link', { name: 'https://example.com/stone' })).toHaveAttribute('rel', 'noopener noreferrer');
  expect(within(first.closest('details')!).queryByText('Unit price')).not.toBeInTheDocument();
  expect(screen.getAllByText('Blue sapphire')).toHaveLength(1);
  // AND closing it restores the compact row.
  fireEvent.click(first);
  expect(screen.getByText('S-123')).not.toBeVisible();
});

it.each(['javascript:alert(1)', 'https://owner:secret@example.com/stone', 'not a URL'])('retains unsafe source text without a link: %s', sourceLink => {
  // GIVEN an archived source value that is not a safe public HTTP link.
  render(<PurchaseContents draft={{ ...draft, entries: [{ ...draft.entries[0], sourceLink }] }} />);
  // WHEN inspecting its line details THEN the value is preserved as noninteractive text.
  fireEvent.click(screen.getByLabelText('Details for line 1'));
  expect(screen.getByText(sourceLink)).toBeVisible();
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
});

it('keeps charge category, reference and notes with the individual charge', () => {
  // GIVEN a charge with supporting information.
  render(<PurchaseContents draft={{ ...draft, charges: [{ id: 'shipping', category: 'shipping', label: 'Freight', amount: '2', payeeKind: 'supplier', payeeName: null, amountStatus: 'estimated', reference: 'Quote 123', notes: 'Express service' }] }} />);
  // WHEN opening that charge THEN all supplemental facts are shown below its primary amount.
  expect(screen.getByText('Quote 123')).not.toBeVisible();
  fireEvent.click(screen.getByLabelText('Details for charge 1'));
  expect(screen.getByText('Shipping / freight')).toBeVisible();
  expect(screen.getByText('Quote 123')).toBeVisible();
  expect(screen.getByText('Express service')).toBeVisible();
  expect(screen.getAllByText('USD 2.00')).toHaveLength(1);
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
