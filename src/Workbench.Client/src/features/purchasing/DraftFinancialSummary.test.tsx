import { fireEvent, render, screen } from '@testing-library/react';
import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { emptyLine } from './draftLine';
import { newCharge } from './draftFinances';
import { zeroAdjustmentCalculation } from '../../test/draftCalculationFixture';
import { DraftFinancialSummary } from './DraftFinancialSummary';

const draft: DraftContent = {
  title: null, supplierName: 'Supplier', supplierId: null, supplierContactName: null,
  supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null,
  supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [],
  entries: [{ ...emptyLine('line'), discount: { mode: 'percentage', value: '10' } }],
  orderDiscount: { mode: 'fixed', value: '1' },
  charges: [newCharge(), { ...newCharge(), payeeKind: 'thirdParty' }],
};
const result: DraftCalculation = {
  ...zeroAdjustmentCalculation({ lines: [{ id: 'line', gross: '100.0000' }], incompleteLineCount: 0, merchandiseEstimate: '100.0000' }),
  lineDiscountTotal: '10.0000', merchandiseNet: '90.0000', orderDiscountAmount: '1.0000',
  supplierCharges: '2.1234', thirdPartyCharges: '3.0000', supplierEstimate: '91.1234', purchaseEstimate: '94.1234',
};
const amount = (label: string) => screen.getByText(label).nextElementSibling;

it('keeps ordered estimates visible and reveals the complete exact arithmetic on demand', () => {
  // GIVEN an ordered purchase with line discounts, order discounts, and both charge payees.
  render(<DraftFinancialSummary draft={draft} result={result} ordered />);
  // THEN both estimates are immediately readable while the breakdown is closed.
  expect(amount('Supplier estimate')).toBeVisible();
  expect(amount('Supplier estimate')).toHaveTextContent('USD 91.1234');
  expect(amount('Total purchase estimate')).toBeVisible();
  expect(amount('Total purchase estimate')).toHaveTextContent('USD 94.1234');
  const toggle = screen.getByText('Estimate breakdown');
  expect(toggle.closest('details')).not.toHaveAttribute('open');
  expect(amount('Merchandise gross')).not.toBeVisible();
  expect(screen.getByText('Estimates only. No payment or balance due is recorded.')).toBeVisible();
  // WHEN expanding the arithmetic THEN every relevant row retains its exact server value.
  fireEvent.click(toggle);
  expect(amount('Merchandise gross')).toBeVisible();
  expect(amount('Merchandise gross')).toHaveTextContent('USD 100.00');
  expect(amount('Line discounts')).toHaveTextContent('−USD 10.00');
  expect(amount('Merchandise after line discounts')).toHaveTextContent('USD 90.00');
  expect(amount('Order discount')).toHaveTextContent('−USD 1.00');
  expect(amount('Supplier charges')).toHaveTextContent('USD 2.1234');
  expect(amount('Third-party charges')).toHaveTextContent('USD 3.00');
});

it('keeps ordered unknown amounts and incomplete-cost warnings outside the disclosure', () => {
  // GIVEN incomplete merchandise and a missing charge amount, with a known zero supplier estimate.
  render(<DraftFinancialSummary draft={draft} result={{ ...result, supplierEstimate: '0.0000', purchaseEstimate: null, incompleteLineCount: 1, incompleteChargeCount: 1 }} ordered />);
  // THEN zero is distinct from unknown and missing-cost warnings remain visible without expansion.
  expect(amount('Supplier estimate')).toHaveTextContent('USD 0.00');
  expect(amount('Total purchase estimate')).toHaveTextContent('Unknown');
  expect(screen.getByText(/1 line needs quantity or pricing details/)).toBeVisible();
  expect(screen.getByText('1 charge has an unknown amount.')).toBeVisible();
  expect(screen.getByText('Known line subtotal')).not.toBeVisible();
});

it('preserves the expanded draft summary and its existing labels', () => {
  // GIVEN the same calculation while the purchase remains a draft.
  render(<DraftFinancialSummary draft={draft} result={result} />);
  // THEN all original arithmetic stays visible without a new disclosure.
  expect(screen.queryByText('Estimate breakdown')).not.toBeInTheDocument();
  expect(amount('Merchandise gross')).toBeVisible();
  expect(amount('Supplier draft estimate')).toBeVisible();
  expect(amount('Third-party charges')).toBeVisible();
  expect(amount('Total purchase estimate')).toHaveTextContent('USD 94.1234');
  expect(screen.getByText('Draft amounts only. No payment or balance due is recorded.')).toBeVisible();
});
