import type { DraftEntry } from '../../api/purchaseOrders';
import { quantityLabel } from './draftLine';
import { formatReferencePrice } from './referencePrice';

export function LegacyPricingDetails({ pricing, currency }: { pricing: NonNullable<DraftEntry['legacyPricing']>; currency: string | null }) {
  return <dl className="po-comparison-details">
    <div><dt>Previous quantity</dt><dd>{quantityLabel(pricing.quantity, pricing.unitOfMeasure)}</dd></div>
    <div><dt>Quoted price</dt><dd>{formatReferencePrice(pricing.unitPrice) ?? 'Unknown'} {currency}</dd></div>
    <div><dt>Quoted basis</dt><dd>{quantityLabel(pricing.pricePerQuantity, pricing.pricingUnit)}</dd></div>
    <div><dt>Previous priced quantity</dt><dd>{quantityLabel(pricing.pricingQuantity, pricing.pricingUnit)}</dd></div>
  </dl>;
}
