import { quantityLabel } from './draftLine';
import { LegacyPricingDetails } from './LegacyPricingDetails';
import { SupplierDetails } from './supplierDetails';
import { supplierSnapshot } from './supplierSnapshot';
import { formatReferencePrice } from './referencePrice';
import type { DraftContent } from '../../api/purchaseOrders';
import { categoryLabel, type Discount } from './draftFinances';
function discountText(discount: Discount | null | undefined, currency: string | null) {
  return discount ? `${discount.mode === 'percentage' ? `${discount.value}%` : `${currency} ${formatReferencePrice(discount.value)}`} on merchandise` : 'None';
}
function SafeLink({ value }: { value: string }) {
  let safe = false;
  try { const url = new URL(value); safe = ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password; } catch { /* Retain invalid local text during comparison. */ }
  return safe ? <a href={value} target="_blank" rel="noopener noreferrer">{value}</a> : <span>{value}</span>;
}
export function DraftComparison({ heading, draft, state = 'Draft' }: { heading: string; draft: DraftContent; state?: string }) {
  return (
    <section className="po-comparison-content">
      <div className="po-comparison-heading"><h3>{heading}</h3><span className={state === 'Ordered' ? 'po-status-badge' : 'po-badge'} data-state={state}>{state}</span></div>
      <dl className="po-comparison-details">
        <div><dt>Supplier</dt><dd>{draft.supplierName ?? 'Not set'}</dd></div>
        <div><dt>Supplier directory link</dt><dd>{draft.supplierId ?? 'One-off'}</dd></div><div><dt>Platform</dt><dd>{draft.platform ?? 'Not set'}</dd></div><div><dt>Supplier order reference</dt><dd>{draft.supplierOrderReference ?? 'Not set'}</dd></div><div><dt>Currency</dt><dd>{draft.currency ?? 'Not set'}</dd></div>
        <div><dt>Custom title (optional)</dt><dd>{draft.title || 'None'}</dd></div>
        <div><dt>Notes</dt><dd>{draft.notes ?? 'None'}</dd></div>
        <div><dt>Order discount</dt><dd>{discountText(draft.orderDiscount, draft.currency)} after line discounts</dd></div>
        <div><dt>Source links</dt><dd>{draft.sourceLinks.length ? (
          <ul>{draft.sourceLinks.map((link, index) => <li key={index}><SafeLink value={link} /></li>)}</ul>
        ) : 'None'}</dd></div>
      </dl>
      <SupplierDetails heading="Supplier contact snapshot" supplier={supplierSnapshot(draft)} /><h4>Order lines</h4>
      {draft.entries.length ? draft.entries.map((entry, index) => (
        <section className="po-comparison-entry" key={entry.id}>
          <h5>Line {index + 1}</h5>
          <dl className="po-comparison-details">
            <div><dt>Description</dt><dd>{entry.description ?? 'Not set'}</dd></div>
            <div><dt>Quantity</dt><dd>{quantityLabel(entry.quantity, entry.unitOfMeasure)}</dd></div>
            <div><dt>Pricing</dt><dd>{entry.priceMode === 'lineTotal' ? 'Total line' : 'Per unit'}</dd></div>
            <div><dt>{entry.priceMode === 'lineTotal' ? 'Total line price' : 'Unit price'}</dt><dd>{formatReferencePrice(entry.price) ?? 'Unknown'} {draft.currency}</dd></div>
            <div><dt>Supplier SKU</dt><dd>{entry.supplierSku ?? 'Not set'}</dd></div>
            <div><dt>Line discount</dt><dd>{discountText(entry.discount, draft.currency)}</dd></div>
            <div><dt>Item type</dt><dd>{entry.itemType ?? 'Not set'}</dd></div>
            <div><dt>Reference price</dt><dd>{formatReferencePrice(entry.indicativePrice) ?? 'Unknown'}</dd></div>
            <div><dt>Notes</dt><dd>{entry.notes ?? 'None'}</dd></div>
            <div><dt>Source link</dt><dd>{entry.sourceLink ? <SafeLink value={entry.sourceLink} /> : 'None'}</dd></div>
          </dl>
          {entry.legacyPricing ? <LegacyPricingDetails pricing={entry.legacyPricing} currency={draft.currency} /> : null}
        </section>
      )) : <p className="po-section-empty">No lines</p>}
      <h4>Charges</h4>
      {(draft.charges ?? []).length ? draft.charges.map(charge => <section className="po-comparison-entry" key={charge.id}><h5>{charge.label || 'Unlabeled charge'}</h5><dl className="po-comparison-details">
        <div><dt>Category</dt><dd>{categoryLabel(charge.category)}</dd></div>
        <div><dt>Amount</dt><dd>{charge.amount === null ? 'Unknown' : `${draft.currency} ${formatReferencePrice(charge.amount)}`}</dd></div>
        <div><dt>Payee</dt><dd>{charge.payeeKind === 'supplier' ? `Supplier: ${draft.supplierName ?? 'Not set'}` : `Third party: ${charge.payeeName ?? 'Not set'}`}</dd></div>
        <div><dt>Amount status</dt><dd>{charge.amountStatus === 'confirmed' ? 'Confirmed from source' : 'Estimated'}</dd></div>
        <div><dt>Supporting reference</dt><dd>{charge.reference ?? 'None'}</dd></div><div><dt>Notes</dt><dd>{charge.notes ?? 'None'}</dd></div>
      </dl></section>) : <p>No charges</p>}
    </section>
  );
}
