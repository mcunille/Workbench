import type { DraftEntry } from '../../api/purchaseOrders';
export const units = [['piece', 'Piece'], ['carat', 'Carat'], ['gram', 'Gram'], ['kilogram', 'Kilogram'], ['ounce', 'Ounce (avoirdupois)'], ['troyOunce', 'Troy ounce'], ['millimeter', 'Millimeter'], ['centimeter', 'Centimeter'], ['meter', 'Meter'], ['parcel', 'Parcel'], ['pair', 'Pair'], ['set', 'Set'], ['pack', 'Pack'], ['box', 'Box'], ['lot', 'Lot']] as const;
export const unitLabel = (unit: string | null) => units.find(([value]) => value === unit)?.[1] ?? 'Unit not set';
export const emptyLine = (id: string): DraftEntry => ({ id, description: null, notes: null, sourceLink: null, indicativePrice: null, quantity: null, unitOfMeasure: null, priceMode: 'perUnit', price: null, legacyPricing: null, supplierSku: null, itemType: null });

export function formatQuantity(value: string | null): string | null {
  if (value === null || !/^\d+(\.\d{1,4})?$/.test(value)) return value;
  const [whole, fraction = ''] = value.split('.');
  const significant = fraction.replace(/0+$/, '');
  return significant ? `${whole}.${significant}` : whole;
}
export function quantityLabel(value: string | null, unit: string | null): string {
  if (value === null) return unit ? `Quantity not set · ${unitLabel(unit)}` : 'Quantity not set';
  const quantity = formatQuantity(value);
  if (!unit) return `${quantity} · unit not set`;
  const singular = unitLabel(unit).toLowerCase();
  const plural = unit === 'box' ? 'boxes' : unit === 'ounce' ? 'ounces (avoirdupois)' : `${singular}s`;
  return `${quantity} ${quantity === '1' ? singular : plural}`;
}

export const hasLinePrice = (entry: DraftEntry) => entry.price !== null || entry.indicativePrice !== null || entry.legacyPricing?.unitPrice != null;
