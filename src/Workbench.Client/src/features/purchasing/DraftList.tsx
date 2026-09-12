import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import { getDrafts, DraftError, type DraftPage } from '../../api/purchaseOrders';
import { DraftMemory } from './draftMemory';
import { Icon } from '../../Icon';
import { FloatingField } from '../../FloatingField';
import './purchasing.css';
interface Props { memory: DraftMemory; follow(event: MouseEvent<HTMLAnchorElement>): void; onAuthLost(): void; }
export function DraftList({ memory, follow, onAuthLost }: Props) {
  const [page, setPage] = useState<DraftPage | undefined>(memory.page);
  const [pending, setPending] = useState<'refresh' | 'more' | null>(null);
  const [message, setMessage] = useState('');
  const [query, setQuery] = useState(memory.query);
  const requestedQuery = useRef(memory.query);
  const sequence = useRef(0);
  const active = useRef(true);
  const inFlight = useRef<'refresh' | 'more' | null>(null);
  const searchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const load = useCallback(async (refresh: boolean, search = refresh ? requestedQuery.current : memory.query) => {
    if (refresh) clearTimeout(searchTimer.current);
    if (!refresh && inFlight.current) return;
    const generation = ++sequence.current; requestedQuery.current = search;
    inFlight.current = refresh ? 'refresh' : 'more'; setPending(inFlight.current); setMessage('');
    try {
      const next = await getDrafts(refresh ? undefined : memory.page?.nextCursor ?? undefined, search || undefined);
      if (!active.current || generation !== sequence.current) return;
      const known = new Set(memory.page?.items.map(item => item.id));
      const result = refresh ? next : { items: [...(memory.page?.items ?? []), ...next.items.filter(item => !known.has(item.id))], nextCursor: next.nextCursor };
      memory.save(result, search); setPage(result);
      if (refresh) memory.savePosition(0);
    } catch (error) {
      if (!active.current || generation !== sequence.current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { memory.invalidate(); setPage(undefined); onAuthLost(); }
      else setMessage(error instanceof DraftError && error.code === 'invalid_cursor' ? 'This page reference is no longer valid. Refresh drafts to start again.' : 'Drafts could not be loaded. Your loaded drafts are kept; try again.');
    } finally {
      if (active.current && generation === sequence.current) { inFlight.current = null; setPending(null); }
    }
  }, [memory, onAuthLost]);
  useEffect(() => {
    active.current = true;
    const requests = sequence;
    if (!memory.page) void load(true);
    return () => { clearTimeout(searchTimer.current); active.current = false; ++requests.current; inFlight.current = null; };
  }, [memory, load]);
  useLayoutEffect(() => {
    if (memory.page && memory.scrollY) window.scrollTo(0, memory.scrollY);
    const remember = () => { memory.savePosition(window.scrollY); };
    window.addEventListener('scroll', remember, { passive: true });
    return () => window.removeEventListener('scroll', remember);
  }, [memory]);
  return (
    <section className="po-list">
      <header className="po-page-heading">
        <div><h1>Purchase orders</h1><p className="lede">Plan a purchase and pick it up later.</p></div>
        <div className="po-page-actions"><a className="quiet button" href="/suppliers" onClick={follow}>Manage suppliers</a><a className="primary button" href="/purchase-orders/new" onClick={follow}><Icon name="plus" />New draft</a></div>
      </header>
      <form className="po-search" onSubmit={event => { event.preventDefault(); void load(true, query.trim()); }}>
        <div className="po-search-controls"><FloatingField htmlFor="po-search" label="Search purchase orders"><input id="po-search" type="search" maxLength={200} value={query} onChange={event => {
          const value = event.target.value; setQuery(value); clearTimeout(searchTimer.current);
          ++sequence.current; inFlight.current = 'refresh'; setPending('refresh'); setMessage('');
          searchTimer.current = setTimeout(() => void load(true, value.trim()), 300);
        }} placeholder="Reference, supplier or title" /></FloatingField>
        <button className="quiet po-search-refresh" type="button" onClick={() => void load(true, query.trim())}>Refresh</button>
        {query || memory.query ? <button type="button" className="quiet po-search-clear" onClick={() => { setQuery(''); void load(true, ''); }}>Clear search</button> : null}</div>
      </form>
      <div className="po-list-toolbar">
        <p className="po-list-caption">{memory.query ? 'Matching draft orders' : 'Draft orders'}</p>
      </div>
      {pending ? <p role="status" className="po-feedback">Loading drafts…</p> : null}
      {message ? <p role="alert" className="po-feedback po-error">{message}</p> : null}
      {page?.items.length === 0 ? (
        <div className="po-empty-state">
          <span className="po-empty-icon"><Icon name="cart" /></span>
          <h2>{memory.query ? 'No matching purchase orders.' : 'No draft orders yet.'}</h2>
          <p>{memory.query ? 'Try a different reference, supplier name or title.' : 'Start with a supplier or a few items. Add the details as you go.'}</p>
        </div>
      ) : null}
      {page && page.items.length > 0 ? (
        <div className="po-draft-panel">
          <div className="po-list-columns" aria-hidden="true"><span>Order</span><span>Supplier</span><span>Last saved</span><span /></div>
          <ul className="po-draft-list">
            {page.items.map(item => (
              <li key={item.id}>
                <a href={`/purchase-orders/${item.id}`} onClick={follow}>
                  <span className="po-order-identity"><span className="po-reference">{item.poReference}</span><strong>{item.title ?? 'Untitled draft'}</strong><span className="po-draft-state">Draft</span></span>
                  <span className="po-order-supplier">{item.supplierName ?? 'Supplier not set'}{item.platform ? <span className="po-row-detail">{item.platform}</span> : null}{item.supplierOrderReference ? <span className="po-row-detail">Supplier ref: {item.supplierOrderReference}</span> : null}</span>
                  <time className="po-order-saved" dateTime={item.updatedAtUtc} title={new Date(item.updatedAtUtc).toLocaleString()}>
                    {new Date(item.updatedAtUtc).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })}
                    <span>{new Date(item.updatedAtUtc).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</span>
                  </time>
                  <Icon name="chevron" />
                </a>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      {page?.nextCursor ? <div className="po-list-footer"><button className="secondary" type="button" disabled={!!pending} onClick={() => void load(false)}>Load more</button></div> : null}
    </section>
  );
}
