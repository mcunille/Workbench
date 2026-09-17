import type { DraftContent, DraftEntry } from '../../api/purchaseOrders';
import { hasLinePrice } from './draftLine';
export type Discount = NonNullable<DraftContent['orderDiscount']>;
export type Charge = DraftContent['charges'][number];
export const chargeCategories = [
  ['Delivery', [['shipping', 'Shipping / freight'], ['handling', 'Handling / packing'], ['insurance', 'Shipping insurance']]],
  ['Taxes and duties', [['salesTax', 'Sales tax'], ['vatGst', 'VAT / GST'], ['customsDuty', 'Customs duty / tariff'], ['otherTax', 'Other tax']]],
  ['Fees and services', [['brokerage', 'Brokerage / customs clearance'], ['paymentFee', 'Payment / bank / currency-conversion fee'], ['inspection', 'Testing / certification / inspection']]],
  ['Other', [['other', 'Other']]],
] as const;
export const categoryLabel = (category: string) => chargeCategories.flatMap(([, values]) => [...values]).find(([value]) => value === category)?.[1] ?? category;
export const newCharge = (): Charge => ({ id: crypto.randomUUID(), category: 'shipping', label: 'Shipping / freight', amount: null, payeeKind: 'supplier', payeeName: null, amountStatus: 'estimated', reference: null, notes: null });
export const financialFieldId = (path: string) => `po-${path.replace(/[^a-zA-Z0-9]/g, '-')}`;
export const hasMonetaryAmounts = (draft: DraftContent) => draft.entries.some(entry => hasLinePrice(entry) || entry.discount?.mode === 'fixed') || draft.orderDiscount?.mode === 'fixed' || draft.charges.some(charge => charge.amount !== null);
export const hasAdjustments = (draft: DraftContent) => !!draft.orderDiscount || draft.entries.some(entry => !!entry.discount) || draft.charges.length > 0;
export function clearDraftAmounts(draft: DraftContent, baseline: DraftContent | undefined, reason: string): DraftContent {
  return { ...draft, orderDiscount: null, entries: draft.entries.map((entry): DraftEntry => ({ ...entry, price: null, indicativePrice: null, legacyPricing: null, discount: null })), charges: draft.charges.map(charge => ({ ...charge, amount: null, amountStatus: 'estimated', notes: baseline?.charges.some(saved => saved.id === charge.id && saved.amountStatus === 'confirmed') ? [charge.notes, reason.trim()].filter(Boolean).join('\n') : charge.notes })) };
}
