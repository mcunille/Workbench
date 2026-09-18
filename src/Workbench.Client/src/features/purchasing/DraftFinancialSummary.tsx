import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { formatReferencePrice } from './referencePrice';
export function DraftFinancialSummary({ draft, result, ordered = false }: { draft: DraftContent; result: DraftCalculation; ordered?: boolean }) {
  const money = (value: string | null | undefined) => value == null ? 'Unknown' : `${draft.currency} ${formatReferencePrice(value)}`;
  const row = (label: string, value: string | null | undefined, reduction = false, className = '') => <div className={className}><dt>{label}</dt><dd>{reduction && value != null ? '−' : ''}{money(value)}</dd></div>;
  const lineDiscounts = draft.entries.some(entry => !!entry.discount);
  const breakdown = <>
    {row(result.incompleteLineCount ? 'Known line subtotal' : 'Merchandise gross', result.merchandiseEstimate, false, 'po-summary-merchandise')}
    {lineDiscounts ? <>{row('Line discounts', result.lineDiscountTotal, true)}{row('Merchandise after line discounts', result.merchandiseNet)}</> : null}
    {draft.orderDiscount ? row('Order discount', result.orderDiscountAmount, true) : null}
    {draft.charges.some(charge => charge.payeeKind === 'supplier') ? row('Supplier charges', result.supplierCharges) : null}
  </>;
  const thirdPartyCharges = draft.charges.some(charge => charge.payeeKind === 'thirdParty') ? row('Third-party charges', result.thirdPartyCharges) : null;
  return <section className="po-financial-summary" aria-labelledby="po-estimate-heading">
    <h3 id="po-estimate-heading">Purchase estimate</h3>
    {!ordered ? <p>Draft amounts only. No payment or balance due is recorded.</p> : null}
    <dl>
      {!ordered ? breakdown : null}
      {row(ordered ? 'Supplier estimate' : 'Supplier draft estimate', result.supplierEstimate, false, 'po-summary-subtotal')}
      {!ordered ? thirdPartyCharges : null}
      {row('Total purchase estimate', result.purchaseEstimate, false, 'po-summary-total')}
    </dl>
    {ordered ? <p>Estimates only. No payment or balance due is recorded.</p> : null}
    {result.incompleteLineCount ? <p>{result.incompleteLineCount} {result.incompleteLineCount === 1 ? 'line needs' : 'lines need'} quantity or pricing details. Any discounts that depend on those details are not included yet.</p> : null}
    {result.incompleteChargeCount ? <p>{result.incompleteChargeCount} {result.incompleteChargeCount === 1 ? 'charge has' : 'charges have'} an unknown amount.</p> : null}
    {!draft.entries.length ? <p>Add merchandise to calculate a complete purchase estimate. Charge subtotals are shown separately.</p> : null}
    {ordered ? <details className="po-estimate-breakdown"><summary>Estimate breakdown</summary><dl>{breakdown}{thirdPartyCharges}</dl></details> : null}
  </section>;
}
