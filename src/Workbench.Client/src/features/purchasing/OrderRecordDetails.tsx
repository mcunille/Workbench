import type { DraftContent } from '../../api/purchaseOrders';
import { Icon } from '../../Icon';
import { SafeSourceLink } from './SafeSourceLink';
import { supplierFields, supplierSnapshot } from './supplierSnapshot';

export function OrderRecordDetails({ draft }: { draft: DraftContent }) {
  const supplier = supplierSnapshot(draft);
  const field = (label: string, value: string | null) => value ? <div key={label}><dt>{label}</dt><dd>{value}</dd></div> : null;
  return <details className="po-inline-details po-order-details">
    <summary aria-label="Order and supplier details"><Icon name="plus" />Details</summary>
    <dl className="po-inline-detail-fields">
      {field('Title', draft.title)}
      {field('Platform', draft.platform)}
      {field('Supplier order reference', draft.supplierOrderReference)}
      {field('Supplier directory reference', draft.supplierId)}
      {field('Currency', draft.currency)}
      {supplierFields.filter(([key]) => key !== 'name' && supplier[key]).map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{key === 'website' ? <SafeSourceLink value={supplier[key]!} /> : supplier[key]}</dd></div>)}
      {field('Order notes', draft.notes)}
      {draft.sourceLinks.length ? <div><dt>Source links</dt><dd><ul>{draft.sourceLinks.map((link, index) => <li key={index}><SafeSourceLink value={link} /></li>)}</ul></dd></div> : null}
    </dl>
  </details>;
}
