import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getOrderRevisions, getOrderRevision, type DraftOrder, type OrderRevision, type OrderRevisionPage } from '../../api/purchaseOrders';
import { DraftComparison } from './DraftComparison';
import { DraftFinancialSummary } from './DraftFinancialSummary';
import { Icon } from '../../Icon';
import { PurchaseContents } from './PurchaseContents';
import { PurchaseChanges } from './PurchaseChanges';
import { PurchaseOrderToolbar } from './PurchaseOrderToolbar';
import { OrderRecordDetails } from './OrderRecordDetails';

export function OrderedPurchase({ order, amend, onCancel, onAuthLost }: { order: DraftOrder; amend(): void; onCancel(): void; onAuthLost(): void }) {
  const [history, setHistory] = useState(false);
  return <section className="editor po-editor po-ordered">
    <PurchaseOrderToolbar className="po-record-toolbar"><button type="button" className="quiet po-back" onClick={onCancel}><Icon name="back" />Purchase orders</button><button type="button" className="primary" onClick={amend}>Create amendment</button></PurchaseOrderToolbar>
    <header className="po-editor-header"><div className="po-heading"><h1>{order.poReference}</h1><span className="po-badge">Ordered</span></div>
      <h2>{order.draft.supplierName}</h2>
      <div className="po-order-meta"><p>Order date <time dateTime={order.orderDate ?? undefined}>{order.orderDate}</time> · Revision {order.revision}</p>
        <button type="button" className="quiet" aria-expanded={history} onClick={() => setHistory(!history)}>{history ? 'Hide history' : 'View history'}</button>
      </div>
      {!history ? <OrderRecordDetails draft={order.draft} /> : null}
    </header>
    {history ? <OrderHistory id={order.id} onAuthLost={onAuthLost} /> : <>
      <PurchaseContents draft={order.draft} calculation={order.calculation} heading="Items" />
      <DraftFinancialSummary draft={order.draft} result={order.calculation} ordered />
    </>}
  </section>;
}

function OrderHistory({ id, onAuthLost }: { id: string; onAuthLost(): void }) {
  const [page, setPage] = useState<OrderRevisionPage>();
  const [selected, setSelected] = useState<OrderRevision>();
  const [previous, setPrevious] = useState<OrderRevision>();
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const active = useRef(true);
  const inFlight = useRef(false);
  const retry = useRef<() => Promise<void>>(() => Promise.resolve());
  function fail(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { setPage(undefined); setSelected(undefined); setPrevious(undefined); onAuthLost(); }
    else setMessage('History could not be loaded. Your current view is kept; retry to load it.');
  }
  async function load(cursor?: string) {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setMessage(''); retry.current = () => load(cursor);
    try { const next = await getOrderRevisions(id, cursor); if (active.current) setPage(old => cursor && old ? { ...next, items: [...old.items, ...next.items.filter(item => !old.items.some(existing => existing.revision === item.revision))] } : next); }
    catch (error) { if (active.current) fail(error); }
    finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  async function select(revision: number) {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setMessage(''); retry.current = () => select(revision);
    try {
      const [current, before] = await Promise.all([getOrderRevision(id, revision), revision > 1 ? getOrderRevision(id, revision - 1) : Promise.resolve(undefined)]);
      if (active.current) { setSelected(current); setPrevious(before); }
    } catch (error) { if (active.current) fail(error); }
    finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  useEffect(() => {
    active.current = true;
    // The history panel is keyed by purchase and mounted only on demand.
    void load();
    return () => { active.current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);
  return <section className="po-form-section" aria-label="Order history">
    <h2>Order history</h2>
    {busy ? <p role="status">Loading history…</p> : null}
    {message ? <><p role="alert">{message}</p><button type="button" className="secondary" disabled={busy} onClick={() => void retry.current()}>Retry history</button></> : null}
    <ol className="po-history-list">{page?.items.map(item => <li key={item.revision}>
      <div><strong>{item.revision === 1 ? 'Original commitment' : `Amendment · revision ${item.revision}`}</strong><p>{item.reason ?? 'Order recorded'} · <time dateTime={item.recordedAtUtc}>{new Date(item.recordedAtUtc).toLocaleString()}</time></p><p className="po-history-actor">Recorded by {item.actorUserId}</p></div>
      <button type="button" className="secondary" disabled={busy} onClick={() => void select(Number(item.revision))}>View revision {item.revision}</button>
    </li>)}</ol>
    {page?.nextCursor ? <button type="button" className="secondary" disabled={busy} onClick={() => void load(page.nextCursor ?? undefined)}>Load more history</button> : null}
    {selected ? <section aria-label={`Revision ${selected.revision} details`}>
      <h2>Revision {selected.revision}</h2><p>{selected.reason ?? 'Original commitment'} · Order date <time dateTime={selected.orderDate}>{selected.orderDate}</time></p>
      {previous ? <PurchaseChanges before={previous.draft} after={selected.draft} beforeDate={previous.orderDate} afterDate={selected.orderDate} beforeCalculation={previous.calculation} afterCalculation={selected.calculation} /> : <><PurchaseContents draft={selected.draft} calculation={selected.calculation} /><DraftFinancialSummary draft={selected.draft} result={selected.calculation} ordered /></>}
      <details className="po-record-details"><summary>Show complete revision contents</summary><div className={previous ? 'po-comparison' : undefined}>
        {previous ? <DraftComparison heading={`Previous revision ${previous.revision}`} draft={previous.draft} state="Ordered" /> : null}
        <DraftComparison heading={`Revision ${selected.revision}`} draft={selected.draft} state="Ordered" />
      </div></details>
    </section> : null}
  </section>;
}
