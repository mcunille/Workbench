import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { categoryLabel, type Discount } from './draftFinances';
import { formatQuantity, quantityLabel } from './draftLine';
import { formatReferencePrice } from './referencePrice';
import { Icon } from '../../Icon';
import { LegacyPricingDetails } from './LegacyPricingDetails';
import { SafeSourceLink } from './SafeSourceLink';

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
        <details className="po-inline-details">
          <summary aria-label={`Details for line ${index + 1}`}><Icon name="plus" />Details</summary>
          {entry.supplierSku || entry.itemType || entry.indicativePrice != null || entry.notes || entry.sourceLink || entry.legacyPricing ? <dl className="po-inline-detail-fields">
            {entry.supplierSku ? <div><dt>Supplier SKU</dt><dd>{entry.supplierSku}</dd></div> : null}
            {entry.itemType ? <div><dt>Item type</dt><dd>{entry.itemType}</dd></div> : null}
            {entry.indicativePrice != null ? <div><dt>Reference price</dt><dd>{money(entry.indicativePrice)}</dd></div> : null}
            {entry.notes ? <div><dt>Notes</dt><dd>{entry.notes}</dd></div> : null}
            {entry.sourceLink ? <div><dt>Source link</dt><dd><SafeSourceLink value={entry.sourceLink} /></dd></div> : null}
          </dl> : <p>No additional details.</p>}
          {entry.legacyPricing ? <LegacyPricingDetails pricing={entry.legacyPricing} currency={draft.currency} /> : null}
        </details>
      </li>)}
    </ul> : <p className="po-section-empty">No lines</p>}
    {draft.orderDiscount ? <p className="po-purchase-discount">Order discount: {discountText(draft.orderDiscount)} on merchandise after line discounts</p> : null}
    {draft.charges.length ? <>
      <h4>Charges</h4>
      <ul className="po-purchase-charges" aria-label="Charges">{draft.charges.map((charge, index) => <li className="po-purchase-charge" key={charge.id}>
        <div><strong>{charge.label || 'Unlabeled charge'}</strong><p className="po-purchase-charge-payee">{charge.payeeKind === 'supplier' ? `Supplier: ${draft.supplierName ?? 'Not set'}` : `Third party: ${charge.payeeName ?? 'Not set'}`}</p></div>
        <div className="po-purchase-charge-amount"><strong>{money(charge.amount)}</strong><p>{charge.amountStatus === 'confirmed' ? 'Confirmed from source' : 'Estimated'}</p></div>
        <details className="po-inline-details">
          <summary aria-label={`Details for charge ${index + 1}`}><Icon name="plus" />Details</summary>
          <dl className="po-inline-detail-fields">
            <div><dt>Category</dt><dd>{categoryLabel(charge.category)}</dd></div>
            {charge.reference ? <div><dt>Supporting reference</dt><dd>{charge.reference}</dd></div> : null}
            {charge.notes ? <div><dt>Notes</dt><dd>{charge.notes}</dd></div> : null}
          </dl>
        </details>
      </li>)}</ul>
    </> : null}
  </section>;
}
