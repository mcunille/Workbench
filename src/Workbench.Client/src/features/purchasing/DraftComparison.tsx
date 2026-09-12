import type { DraftContent } from '../../api/purchaseOrders';
function SafeLink({ value }: { value: string }) {
  let safe = false;
  try { const url = new URL(value); safe = ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password; } catch { /* Retain invalid local text during comparison. */ }
  return safe ? <a href={value} target="_blank" rel="noopener noreferrer">{value}</a> : <span>{value}</span>;
}
export function DraftComparison({ heading, draft }: { heading: string; draft: DraftContent }) {
  return <section className="po-comparison-content"><h3>{heading}</h3><dl>
    <dt>Title</dt><dd>{draft.title ?? 'Untitled draft'}</dd><dt>Supplier</dt><dd>{draft.supplierName ?? 'Not set'}</dd>
    <dt>Currency</dt><dd>{draft.currency ?? 'Not set'}</dd><dt>Notes</dt><dd>{draft.notes ?? 'None'}</dd>
    <dt>Source links</dt><dd>{draft.sourceLinks.length ? <ul>{draft.sourceLinks.map((link, index) => <li key={index}><SafeLink value={link} /></li>)}</ul> : 'None'}</dd>
  </dl><h4>Shopping list</h4>{draft.entries.length ? draft.entries.map((entry, index) => <section key={entry.id}><h5>Entry {index + 1}</h5><dl>
    <dt>Description</dt><dd>{entry.description ?? 'Not set'}</dd><dt>Reference price</dt><dd>{entry.indicativePrice ?? 'Unknown'}</dd>
    <dt>Notes</dt><dd>{entry.notes ?? 'None'}</dd><dt>Source link</dt><dd>{entry.sourceLink ? <SafeLink value={entry.sourceLink} /> : 'None'}</dd>
  </dl></section>) : <p>No entries</p>}</section>;
}
