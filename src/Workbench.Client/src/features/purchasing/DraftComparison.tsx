import { formatReferencePrice } from './referencePrice';
import type { DraftContent } from '../../api/purchaseOrders';
function SafeLink({ value }: { value: string }) {
  let safe = false;
  try { const url = new URL(value); safe = ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password; } catch { /* Retain invalid local text during comparison. */ }
  return safe ? <a href={value} target="_blank" rel="noopener noreferrer">{value}</a> : <span>{value}</span>;
}
export function DraftComparison({ heading, draft }: { heading: string; draft: DraftContent }) {
  return (
    <section className="po-comparison-content">
      <div className="po-comparison-heading"><h3>{heading}</h3><span className="po-badge">Draft</span></div>
      <dl className="po-comparison-details">
        <div><dt>Title</dt><dd>{draft.title ?? 'Untitled draft'}</dd></div>
        <div><dt>Supplier</dt><dd>{draft.supplierName ?? 'Not set'}</dd></div>
        <div><dt>Currency</dt><dd>{draft.currency ?? 'Not set'}</dd></div>
        <div><dt>Notes</dt><dd>{draft.notes ?? 'None'}</dd></div>
        <div><dt>Source links</dt><dd>{draft.sourceLinks.length ? (
          <ul>{draft.sourceLinks.map((link, index) => <li key={index}><SafeLink value={link} /></li>)}</ul>
        ) : 'None'}</dd></div>
      </dl>
      <h4>Shopping list</h4>
      {draft.entries.length ? draft.entries.map((entry, index) => (
        <section className="po-comparison-entry" key={entry.id}>
          <h5>Entry {index + 1}</h5>
          <dl className="po-comparison-details">
            <div><dt>Description</dt><dd>{entry.description ?? 'Not set'}</dd></div>
            <div><dt>Reference price</dt><dd>{formatReferencePrice(entry.indicativePrice) ?? 'Unknown'}</dd></div>
            <div><dt>Notes</dt><dd>{entry.notes ?? 'None'}</dd></div>
            <div><dt>Source link</dt><dd>{entry.sourceLink ? <SafeLink value={entry.sourceLink} /> : 'None'}</dd></div>
          </dl>
        </section>
      )) : <p className="po-section-empty">No entries</p>}
    </section>
  );
}
