import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import { getDrafts, DraftError, type DraftPage } from '../../api/purchaseOrders';
import { DraftMemory } from './draftMemory';
import './purchasing.css';
interface Props { memory: DraftMemory; follow(event: MouseEvent<HTMLAnchorElement>): void; onAuthLost(): void; }
export function DraftList({ memory, follow, onAuthLost }: Props) {
  const [page, setPage] = useState<DraftPage | undefined>(memory.page);
  const [pending, setPending] = useState<'refresh' | 'more' | null>(null);
  const [message, setMessage] = useState('');
  const sequence = useRef(0);
  const active = useRef(true);
  const inFlight = useRef<'refresh' | 'more' | null>(null);
  const load = useCallback(async (refresh: boolean) => {
    if (inFlight.current === 'refresh' || (!refresh && inFlight.current)) return;
    const generation = ++sequence.current;
    inFlight.current = refresh ? 'refresh' : 'more'; setPending(inFlight.current); setMessage('');
    try {
      const next = await getDrafts(refresh ? undefined : memory.page?.nextCursor ?? undefined);
      if (!active.current || generation !== sequence.current) return;
      const known = new Set(memory.page?.items.map(item => item.id));
      const result = refresh ? next : { items: [...(memory.page?.items ?? []), ...next.items.filter(item => !known.has(item.id))], nextCursor: next.nextCursor };
      memory.save(result); setPage(result);
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
    return () => { active.current = false; ++requests.current; inFlight.current = null; };
  }, [memory, load]);
  useLayoutEffect(() => {
    if (memory.page && memory.scrollY) window.scrollTo(0, memory.scrollY);
    const remember = () => { memory.savePosition(window.scrollY); };
    window.addEventListener('scroll', remember, { passive: true });
    return () => window.removeEventListener('scroll', remember);
  }, [memory]);
  return <section className="po-list"><h1>Purchase orders</h1><p className="lede">Plan a purchase and pick it up later.</p>
    <div className="button-row"><a className="primary" href="/purchase-orders/new" onClick={follow}>New draft</a><button className="secondary" type="button" disabled={pending === 'refresh'} onClick={() => void load(true)}>Refresh</button></div>
    {pending ? <p role="status">Loading drafts…</p> : null}
    {message ? <p role="alert">{message}</p> : null}
    {page?.items.length === 0 ? <p>No draft orders yet.</p> : null}
    <ul className="po-draft-list">{page?.items.map(item => <li key={item.id}><a href={`/purchase-orders/${item.id}`} onClick={follow}><strong>{item.title ?? 'Untitled draft'}</strong><span>{item.supplierName ?? 'Supplier not set'}</span><span>Saved {new Date(item.updatedAtUtc).toLocaleString()}</span></a></li>)}</ul>
    {page?.nextCursor ? <button className="secondary" type="button" disabled={!!pending} onClick={() => void load(false)}>Load more</button> : null}
  </section>;
}
