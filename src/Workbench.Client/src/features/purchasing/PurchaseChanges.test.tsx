import { render, screen, within } from '@testing-library/react';
import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { emptyLine } from './draftLine';
import { newCharge } from './draftFinances';
import { PurchaseChanges } from './PurchaseChanges';

const baseline = (): DraftContent => ({ title: 'Gems', supplierName: 'Supplier', currency: 'USD', notes: null, sourceLinks: [], entries: [], supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, orderDiscount: null, charges: [] });

it('pairs only changed fields and distinguishes unknown amounts from explicit zero', () => {
  // GIVEN a known line with an unknown price and a changed supplier snapshot.
  const before = { ...baseline(), entries: [emptyLine('a')] };
  const after = { ...before, supplierEmail: 'orders@example.test', entries: [{ ...before.entries[0], price: '0.0000' }] };
  // WHEN comparing the proposed purchase.
  render(<PurchaseChanges before={before} after={after} />);
  // THEN changed fields have adjacent before/after values and unchanged fields are omitted.
  expect(screen.getByText('2 fields changed')).toBeVisible();
  expect(screen.queryByText('Title')).not.toBeInTheDocument();
  const price = screen.getByText('Line 1 · Price').closest('li')!;
  expect(within(price).getByText('Unknown')).toBeVisible();
  expect(within(price).getByText('USD 0.00')).toBeVisible();
  expect(screen.getByText('orders@example.test')).toBeVisible();
});

it('matches stable identities, reports reordering, and includes line and charge additions and removals', () => {
  // GIVEN two saved lines and a charge.
  const before = { ...baseline(), entries: [{ ...emptyLine('a'), description: 'Ruby' }, { ...emptyLine('b'), description: 'Pearl' }], charges: [{ ...newCharge(), id: 'old', label: 'Freight' }] };
  const after = { ...before, entries: [before.entries[1], { ...before.entries[0], supplierSku: 'SKU-2' }, { ...emptyLine('c'), description: 'Opal' }], charges: [{ ...newCharge(), id: 'new', label: 'Insurance' }] };
  // WHEN entries move and different charge identities replace one another.
  render(<PurchaseChanges before={before} after={after} />);
  // THEN movement is explicit and metadata remains attached to the original identity.
  expect(screen.getByText('Line order')).toBeVisible();
  expect(screen.getByText('Line 2 · Supplier SKU')).toBeVisible();
  expect(screen.getByText('Opal')).toBeVisible();
  expect(screen.getAllByText('Added').length).toBeGreaterThan(0);
  expect(screen.getAllByText('Removed').length).toBeGreaterThan(0);
  expect(screen.getByText('Freight')).toBeVisible();
  expect(screen.getByText('Insurance')).toBeVisible();
});

it('does not treat equivalent decimal formatting or absent optional metadata as changes', () => {
  // GIVEN semantically equivalent quantities, discounts, and null/absent metadata.
  const before = { ...baseline(), entries: [{ ...emptyLine('a'), quantity: '1', price: '2', discount: { mode: 'percentage', value: '5' } }] };
  const after = { ...before, notes: undefined, entries: [{ ...before.entries[0], quantity: '1.0000', price: '2.0000', discount: { mode: 'percentage', value: '5.0000' } }] } as unknown as DraftContent;
  // WHEN comparing representations of the same purchase.
  render(<PurchaseChanges before={before} after={after} />);
  // THEN no changed fields are invented.
  expect(screen.getByText('No fields changed')).toBeVisible();
  expect(screen.queryByRole('list')).not.toBeInTheDocument();
});

it('includes order dates, discounts, source links, and retained legacy pricing', () => {
  // GIVEN a purchase with retained legacy information.
  const before = { ...baseline(), entries: [emptyLine('a')] };
  const after = { ...before, sourceLinks: ['javascript:alert(1)'], orderDiscount: { mode: 'fixed', value: '3' }, entries: [{ ...before.entries[0], legacyPricing: { quantity: '2', unitOfMeasure: 'carat', unitPrice: '4', pricingUnit: 'gram', pricePerQuantity: '5', pricingQuantity: '6' } }] };
  // WHEN reviewing all supported metadata changes.
  render(<PurchaseChanges before={before} after={after} beforeDate="2026-09-16" afterDate="2026-09-17" />);
  // THEN legacy fields and date are explicit and source text cannot become an unsafe link.
  expect(screen.getByText('Order date')).toBeVisible();
  expect(screen.getByText('Order discount · Value')).toBeVisible();
  expect(screen.getByText('Line 1 · Legacy pricing · Pricing quantity')).toBeVisible();
  expect(screen.getByText('javascript:alert(1)')).toBeVisible();
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
});

it.each([
  ['title', 'Title'], ['supplierName', 'Supplier'], ['supplierId', 'Supplier directory link'],
  ['supplierContactName', 'Supplier contact name'], ['supplierEmail', 'Supplier email'],
  ['supplierPhone', 'Supplier phone'], ['supplierWebsite', 'Supplier website'],
  ['supplierPostalAddress', 'Supplier postal address'], ['supplierOrderReference', 'Supplier order reference'],
  ['platform', 'Platform'], ['notes', 'Notes'], ['currency', 'Currency'],
] as const)('shows the changed %s metadata without hiding supplier snapshot changes', (key, label) => {
  // GIVEN a saved purchase whose single metadata field is revised.
  const before = baseline();
  // WHEN reviewing the proposed edit.
  render(<PurchaseChanges before={before} after={{ ...before, [key]: 'Revised value' }} />);
  // THEN its descriptive label, value, and exact field count are visible.
  expect(screen.getByText(label, { selector: 'strong' })).toBeVisible();
  expect(screen.getByText('Revised value')).toBeVisible();
  expect(screen.getByText('1 field changed')).toBeVisible();
});

it('preserves charge status, payee, reference and notes in a stable identity comparison', () => {
  // GIVEN an estimated supplier charge with a known amount.
  const charge = { ...newCharge(), id: 'freight', amount: '10' };
  const before = { ...baseline(), charges: [charge] };
  const after = { ...before, charges: [{ ...charge, category: 'handling', payeeKind: 'thirdParty', payeeName: 'Courier', amountStatus: 'confirmed', reference: 'Invoice 22', notes: 'Agreed delivery' }] };
  // WHEN the same charge receives new evidence and a third-party payee.
  render(<PurchaseChanges before={before} after={after} />);
  // THEN every changed attribute is readable and the unchanged amount is omitted.
  expect(screen.getByText('6 fields changed')).toBeVisible();
  for (const label of ['Category', 'Payee', 'Payee name', 'Amount status', 'Supporting reference', 'Notes']) expect(screen.getByText(`Charge 1 · ${label}`)).toBeVisible();
  expect(screen.getByText('Confirmed from source')).toBeVisible();
  expect(screen.getByText('Third party')).toBeVisible();
  expect(screen.queryByText('Charge 1 · Amount')).not.toBeInTheDocument();
});

it('shows before and after totals while distinguishing missing calculations, unknown totals, and zero', () => {
  // GIVEN a new calculation with an unknown supplier estimate and explicit zero purchase estimate.
  const calculation: DraftCalculation = { lines: [], incompleteLineCount: 0, merchandiseEstimate: null, lineDiscountTotal: null, merchandiseNet: null, orderDiscountBase: null, orderDiscountAmount: null, discountedMerchandise: null, supplierCharges: null, thirdPartyCharges: null, supplierEstimate: null, purchaseEstimate: '0.0000', incompleteChargeCount: 0 };
  // WHEN the baseline calculation is unavailable.
  const { rerender } = render(<PurchaseChanges before={baseline()} after={baseline()} beforeCalculation={null} afterCalculation={calculation} />);
  // THEN unavailable calculation, unknown amount and zero remain distinct.
  expect(screen.getAllByText('Calculation unavailable')).toHaveLength(2);
  expect(screen.getByText('Unknown')).toBeVisible();
  expect(screen.getByText('USD 0.00')).toBeVisible();
  // WHEN the baseline estimates are available.
  rerender(<PurchaseChanges before={baseline()} after={baseline()} beforeCalculation={{ ...calculation, supplierEstimate: '12', purchaseEstimate: '14' }} afterCalculation={calculation} />);
  // THEN both baseline totals are available alongside the proposed estimates.
  expect(screen.getByText('USD 12.00')).toBeVisible();
  expect(screen.getByText('USD 14.00')).toBeVisible();
  expect(screen.queryByText('Calculation unavailable')).not.toBeInTheDocument();
});
