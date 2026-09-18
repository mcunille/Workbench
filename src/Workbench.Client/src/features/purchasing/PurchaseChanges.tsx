import type { DraftCalculation, DraftContent } from '../../api/purchaseOrders';
import { categoryLabel } from './draftFinances';
import { formatQuantity, unitLabel } from './draftLine';
import { formatReferencePrice } from './referencePrice';

export interface PurchaseChangesProps {
  /** Immutable baseline and proposed content; entries and charges are matched by ID. */
  before: DraftContent;
  after: DraftContent;
  beforeDate?: string | null;
  afterDate?: string | null;
  beforeCalculation?: DraftCalculation | null;
  afterCalculation?: DraftCalculation | null;
  heading?: string;
}

type Field = { label: string; value: string | null; display: string };
type Fields = Map<string, Field>;
const contentLabels = {
  title: 'Title', supplierName: 'Supplier', currency: 'Currency', notes: 'Notes', sourceLinks: 'Source links',
  supplierId: 'Supplier directory link', supplierContactName: 'Supplier contact name', supplierEmail: 'Supplier email',
  supplierPhone: 'Supplier phone', supplierWebsite: 'Supplier website', supplierPostalAddress: 'Supplier postal address',
  supplierOrderReference: 'Supplier order reference', platform: 'Platform', orderDiscount: 'Order discount',
} satisfies Record<Exclude<keyof DraftContent, 'entries' | 'charges'>, string>;
const entryLabels = {
  description: 'Description', notes: 'Notes', sourceLink: 'Source link', indicativePrice: 'Reference price',
  quantity: 'Quantity', unitOfMeasure: 'Unit', priceMode: 'Pricing', price: 'Price', legacyPricing: 'Legacy pricing',
  supplierSku: 'Supplier SKU', itemType: 'Item type', discount: 'Line discount',
} satisfies Record<Exclude<keyof DraftContent['entries'][number], 'id'>, string>;
const chargeLabels = {
  category: 'Category', label: 'Label', amount: 'Amount', payeeKind: 'Payee', payeeName: 'Payee name',
  amountStatus: 'Amount status', reference: 'Supporting reference', notes: 'Notes',
} satisfies Record<Exclude<keyof DraftContent['charges'][number], 'id'>, string>;
const legacyLabels = {
  quantity: 'Quantity', unitOfMeasure: 'Unit', unitPrice: 'Unit price', pricingUnit: 'Pricing unit',
  pricePerQuantity: 'Price per quantity', pricingQuantity: 'Pricing quantity',
} satisfies Record<keyof NonNullable<DraftContent['entries'][number]['legacyPricing']>, string>;
const numericFields = new Set(['quantity', 'price', 'indicativePrice', 'amount', 'value', 'unitPrice', 'pricePerQuantity', 'pricingQuantity']);
const moneyFields = new Set(['price', 'indicativePrice', 'amount', 'unitPrice', 'pricePerQuantity']);

// Normalize decimals as strings so large exact values never pass through floating point.
function canonical(value: string | null | undefined, numeric = false): string | null {
  if (value == null || value === '') return null;
  if (!numeric || !/^\d+(\.\d+)?$/.test(value)) return value;
  const [whole, fraction = ''] = value.split('.');
  const significant = fraction.replace(/0+$/, '');
  return `${whole.replace(/^0+(?=\d)/, '')}${significant ? `.${significant}` : ''}`;
}
function money(value: string | null | undefined, currency: string | null): string {
  return value == null ? 'Unknown' : `${currency ?? 'Currency not set'} ${formatReferencePrice(canonical(value, true))}`;
}
function collect(draft: DraftContent): Fields {
  const fields: Fields = new Map();
  function record(key: string, label: string, value: unknown, field: string, container?: Record<string, unknown>) {
    if (field === 'discount' || field === 'orderDiscount' || field === 'legacyPricing') {
      const object = (value ?? {}) as Record<string, unknown>;
      const labels = field === 'legacyPricing' ? legacyLabels : { mode: 'Type', value: 'Value' };
      for (const [child, childLabel] of Object.entries(labels)) record(`${key}.${child}`, `${label} · ${childLabel}`, object[child], child, object);
      return;
    }
    const raw = Array.isArray(value) ? value.join('\n') : typeof value === 'string' ? value : null;
    const normalized = canonical(raw, numericFields.has(field));
    let display = normalized ?? (numericFields.has(field) ? 'Unknown' : 'Not set');
    if (moneyFields.has(field) || (field === 'value' && container?.mode === 'fixed')) display = money(normalized, draft.currency);
    else if (normalized !== null) {
      if (field === 'value' && container?.mode === 'percentage') display = `${formatQuantity(normalized)}%`;
      else if (field === 'quantity' || field === 'pricingQuantity') display = formatQuantity(normalized)!;
      else if (field === 'unitOfMeasure') display = unitLabel(normalized);
      else if (field === 'category') display = categoryLabel(normalized);
      else if (field === 'priceMode') display = normalized === 'lineTotal' ? 'Total line' : normalized === 'perUnit' ? 'Per unit' : normalized;
      else if (field === 'amountStatus') display = normalized === 'confirmed' ? 'Confirmed from source' : normalized === 'estimated' ? 'Estimated' : normalized;
      else if (field === 'payeeKind') display = normalized === 'supplier' ? 'Supplier' : normalized === 'thirdParty' ? 'Third party' : normalized;
      else if (field === 'mode') display = normalized === 'fixed' ? 'Fixed amount' : normalized === 'percentage' ? 'Percentage' : normalized;
    }
    fields.set(key, { label, value: normalized, display });
  }
  for (const [key, label] of Object.entries(contentLabels)) record(key, label, draft[key as keyof typeof contentLabels], key);
  for (const [index, entry] of draft.entries.entries()) {
    const prefix = `entries.${entry.id}`;
    fields.set(prefix, { label: `Line ${index + 1}`, value: 'Present', display: 'Present' });
    for (const [key, label] of Object.entries(entryLabels)) record(`${prefix}.${key}`, `Line ${index + 1} · ${label}`, entry[key as keyof typeof entryLabels], key);
  }
  for (const [index, charge] of (draft.charges ?? []).entries()) {
    const prefix = `charges.${charge.id}`;
    fields.set(prefix, { label: `Charge ${index + 1}`, value: 'Present', display: 'Present' });
    for (const [key, label] of Object.entries(chargeLabels)) record(`${prefix}.${key}`, `Charge ${index + 1} · ${label}`, charge[key as keyof typeof chargeLabels], key);
  }
  return fields;
}

function ValuePair({ before, after }: { before: string; after: string }) {
  return <dl className="po-change-values"><div className="po-change-value"><dt>Before</dt><dd>{before}</dd></div><div className="po-change-value"><dt>After</dt><dd>{after}</dd></div></dl>;
}

export function PurchaseChanges({ before, after, beforeDate, afterDate, beforeCalculation, afterCalculation, heading = 'Changes' }: PurchaseChangesProps) {
  const oldFields = collect(before);
  const newFields = collect(after);
  if (beforeDate !== undefined || afterDate !== undefined) {
    oldFields.set('orderDate', { label: 'Order date', value: canonical(beforeDate), display: beforeDate || 'Not set' });
    newFields.set('orderDate', { label: 'Order date', value: canonical(afterDate), display: afterDate || 'Not set' });
  }
  for (const [key, label] of [['entries', 'Line order'], ['charges', 'Charge order']] as const) {
    const oldIds = new Set(before[key].map(item => item.id));
    const newIds = new Set(after[key].map(item => item.id));
    const oldOrder = before[key].filter(item => newIds.has(item.id)).map(item => item.id);
    const newOrder = after[key].filter(item => oldIds.has(item.id)).map(item => item.id);
    if (oldOrder.join('|') !== newOrder.join('|')) {
      const names = new Map(before[key].map((item, index) => [item.id, `${key === 'entries' ? 'Line' : 'Charge'} ${index + 1}`]));
      oldFields.set(`${key}.order`, { label, value: oldOrder.join('|'), display: oldOrder.map(id => names.get(id)).join(' → ') });
      newFields.set(`${key}.order`, { label, value: newOrder.join('|'), display: newOrder.map(id => names.get(id)).join(' → ') });
    }
  }
  const changes = [...new Set([...oldFields.keys(), ...newFields.keys()])].flatMap(key => {
    const oldField = oldFields.get(key);
    const newField = newFields.get(key);
    if ((oldField?.value ?? null) === (newField?.value ?? null)) return [];
    return [{ key, label: newField?.label ?? oldField!.label, before: oldField?.display ?? 'Not present', after: newField?.display ?? 'Not present', kind: oldField?.value == null ? 'Added' : newField?.value == null ? 'Removed' : 'Changed' }];
  });
  return <section className="po-changes" aria-label={heading}>
    <div className="po-changes-heading"><h3>{heading}</h3><p>{changes.length === 0 ? 'No fields changed' : `${changes.length} ${changes.length === 1 ? 'field' : 'fields'} changed`}</p></div>
    {changes.length > 0 ? <ul className="po-change-list">{changes.map(change => <li key={change.key} className="po-change-row"><div className="po-change-label"><strong>{change.label}</strong><span className="po-change-kind">{change.kind}</span></div><ValuePair before={change.before} after={change.after} /></li>)}</ul> : null}
    {beforeCalculation !== undefined || afterCalculation !== undefined ? <div className="po-change-totals">{(['supplierEstimate', 'purchaseEstimate'] as const).map(key => <div key={key}><h4>{key === 'supplierEstimate' ? 'Supplier estimate' : 'Total purchase estimate'}</h4><ValuePair before={beforeCalculation ? money(beforeCalculation[key], before.currency) : 'Calculation unavailable'} after={afterCalculation ? money(afterCalculation[key], after.currency) : 'Calculation unavailable'} /></div>)}</div> : null}
  </section>;
}
