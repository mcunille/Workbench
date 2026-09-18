import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { type Discount } from './draftFinances';
import { formatQuantity, quantityLabel } from './draftLine';
import { formatReferencePrice } from './referencePrice';

export function PurchaseContents({ draft, calculation, heading = 'Agreed contents' }: { draft: DraftContent; calculation?: DraftCalculation; heading?: string }) {
  const money = (value: string | null | undefined) => value == null ? 'Unknown' : `${draft.currency ?? 'Currency not set'} ${formatReferencePrice(value)}`;
  const discountText = (discount: Discount) => discount.mode === 'percentage' ? `${formatQuantity(discount.value)}%` : money(discount.value);
  const calculatedLines = new Map(calculation?.lines.map(line => [line.id, line]));

  return <section className="po-purchase-contents" aria-label={heading}>
    <h3>{heading}</h3>
    {draft.entries.length ? <ul className="po-purchase-lines" aria-label="Order lines">
      {draft.entries.map((entry, index) => <li className="po-purchase-line" key={entry.id}>
        <div className="po-purchase-line-description"><h4>{entry.description || `Line ${index + 1} · description not set`}</h4><p>{quantityLabel(entry.quantity, entry.unitOfMeasure)}</p></div>
        <dl className="po-purchase-line-amounts">
          <div><dt>{entry.priceMode === 'lineTotal' ? 'Total line price' : 'Unit price'}</dt><dd>{money(entry.price)}</dd></div>
          {entry.discount ? <div><dt>Line discount</dt><dd>{discountText(entry.discount)}</dd></div> : null}
          {calculation ? <div><dt>Line estimate</dt><dd>{money(calculatedLines.get(entry.id)?.net)}</dd></div> : null}
        </dl>
      </li>)}
    </ul> : <p className="po-section-empty">No lines</p>}
    {draft.orderDiscount ? <p className="po-purchase-discount">Order discount: {discountText(draft.orderDiscount)} on merchandise after line discounts</p> : null}
    {draft.charges.length ? <>
      <h4>Charges</h4>
      <ul className="po-purchase-charges" aria-label="Charges">{draft.charges.map(charge => <li className="po-purchase-charge" key={charge.id}>
        <div><strong>{charge.label || 'Unlabeled charge'}</strong><p className="po-purchase-charge-payee">{charge.payeeKind === 'supplier' ? `Supplier: ${draft.supplierName ?? 'Not set'}` : `Third party: ${charge.payeeName ?? 'Not set'}`}</p></div>
        <div className="po-purchase-charge-amount"><strong>{money(charge.amount)}</strong><p>{charge.amountStatus === 'confirmed' ? 'Confirmed from source' : 'Estimated'}</p></div>
      </li>)}</ul>
    </> : null}
  </section>;
}
