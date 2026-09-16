import { useCallback, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import { getSuppliers, type Supplier, type SupplierPage } from '../../api/suppliers';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import { SupplierMemory } from './supplierMemory';
import './purchasing.css';
const loadingSuppliers = 'Loading suppliers…';
interface Props { memory?: SupplierMemory; follow?(event: MouseEvent<HTMLAnchorElement>): void; onSelect?(supplier: Supplier): void; onAuthLost(): void; }
export function SupplierList({ memory: suppliedMemory, follow, onSelect, onAuthLost }: Props) {
  const [localMemory] = useState(() => new SupplierMemory());
  const memory = !onSelect && suppliedMemory ? suppliedMemory : localMemory;
  const [page, setPage] = useState<SupplierPage | undefined>(memory.page);
  const latestPage = useRef(memory.page);
  const [query, setQuery] = useState(memory.query);
  const [loadedQuery, setLoadedQuery] = useState(memory.query);
  const [archived, setArchived] = useState(memory.archived);
  const loadedFilter = useRef({ query: memory.query, archived: memory.archived });
  const searchInput = useRef<HTMLInputElement>(null);
  const [pending, setPending] = useState(!memory.page || memory.needsRefresh);
  const [message, setMessage] = useState('');
  const sequence = useRef(0);
  const active = useRef(true);
  const searchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const load = useCallback(async (more = false, search = loadedFilter.current.query, includeArchived = loadedFilter.current.archived, restore = !more && memory.needsRefresh && search === memory.query && includeArchived === memory.archived) => {
    if (!more) clearTimeout(searchTimer.current);
    const generation = ++sequence.current; setPending(true); setMessage('');
    try {
      let result = await getSuppliers(more ? latestPage.current?.nextCursor ?? undefined : undefined, search || undefined, includeArchived);
      const wanted = restore ? memory.page?.items.length ?? 0 : 0;
      while (result.nextCursor && result.items.length < wanted) {
        if (!active.current || generation !== sequence.current) return;
        const continuation = await getSuppliers(result.nextCursor, search || undefined, includeArchived);
        result = { items: [...result.items, ...continuation.items], nextCursor: continuation.nextCursor };
      }
      if (!active.current || generation !== sequence.current) return;
      const known = new Set(latestPage.current?.items.map(item => item.id));
      const next = more ? { ...result, items: [...(latestPage.current?.items ?? []), ...result.items.filter(item => !known.has(item.id))] } : result;
      memory.save(next, search, includeArchived); if (!more && !restore) memory.savePosition(0);
      loadedFilter.current = { query: search, archived: includeArchived }; latestPage.current = next; setPage(next); setLoadedQuery(search);
    } catch (error) {
      if (!active.current || generation !== sequence.current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { memory.clear(); latestPage.current = undefined; setPage(undefined); onAuthLost(); }
      else setMessage('Suppliers could not be loaded. Loaded suppliers are kept; retry your search or refresh.');
    } finally { if (active.current && generation === sequence.current) setPending(false); }
  }, [memory, onAuthLost]);
  useEffect(() => { active.current = true; const requests = sequence; let mounted = true; queueMicrotask(() => { if (mounted) { memory.invalidate(); void load(false, memory.query, memory.archived, true); } }); return () => { clearTimeout(searchTimer.current); mounted = false; active.current = false; ++requests.current; }; }, [load, memory]);
  useLayoutEffect(() => {
    if (onSelect) return;
    if (memory.page && memory.scrollY) window.scrollTo(0, memory.scrollY);
    const remember = () => memory.savePosition(window.scrollY);
    window.addEventListener('scroll', remember, { passive: true });
    return () => window.removeEventListener('scroll', remember);
  }, [memory, onSelect]);
  return <section className="po-list" aria-label="Supplier directory">
    {!onSelect ? <>
    <a className="quiet button po-back" href="/purchase-orders" onClick={follow}><Icon name="back" />Back to purchase orders</a><header className="po-page-heading"><div><h1>Suppliers</h1><p className="lede">Your contacts for future purchases.</p></div><a href="/suppliers/new" onClick={follow} className="primary button"><Icon name="plus" />New supplier</a></header></> : null}
    <form className="po-search po-draft-search" onSubmit={event => { event.preventDefault(); event.stopPropagation(); void load(false, query.trim(), archived); }}>
      <div className="po-search-controls"><FloatingField htmlFor="supplier-search" label="Search suppliers"><input ref={searchInput} type="search" id="supplier-search" maxLength={200} value={query} onChange={event => {
        const value = event.target.value; setQuery(value); clearTimeout(searchTimer.current);
        ++sequence.current; setPending(true); setMessage('');
        searchTimer.current = setTimeout(() => void load(false, value.trim(), archived), 300);
      }} placeholder="Supplier name" /></FloatingField>
      <button type="button" className="quiet po-search-refresh" aria-label="Refresh suppliers" onClick={() => void load(false, query.trim(), archived)}>Refresh</button>
      </div>
      {!onSelect ? <label className="po-archive-filter"><input type="checkbox" checked={archived} onChange={event => { setArchived(event.target.checked); void load(false, query.trim(), event.target.checked); }} />Include archived suppliers</label> : null}
    </form>
    <div className="po-list-toolbar po-draft-results-toolbar po-supplier-results-toolbar">
      <div className="po-draft-result-context">
        <div className="po-draft-progress"><span className="po-supplier-progress-space" aria-hidden="true">{loadingSuppliers}</span><p role="status" aria-live="polite" aria-atomic="true" className={pending ? undefined : 'po-accessible-heading'}>{pending ? loadingSuppliers : message || !page ? '' : page.items.length === 0 ? loadedQuery ? 'No matching suppliers.' : 'No suppliers yet.' : `Suppliers shown: ${page.items.length.toLocaleString()}.${page.nextCursor ? ' More available.' : ''}`}</p></div>
      </div>
      <button className={`quiet po-draft-clear${query || loadedQuery ? '' : ' is-unavailable'}`} type="button" disabled={!query && !loadedQuery} aria-hidden={!query && !loadedQuery} onClick={() => { searchInput.current?.focus(); setQuery(''); void load(false, '', archived); }}>Clear search</button>
    </div>
    {message ? <p role="alert" className="po-feedback po-error">{message}</p> : null}
    {page?.items.length === 0 ? <p className="po-empty-state">{loadedQuery ? 'No matching suppliers.' : 'No suppliers yet.'}</p> : null}
    <ul className="po-supplier-list">{page?.items.map(item => {
      const details = <div><strong>{item.supplier.name}</strong>{item.isArchived ? <span className="po-badge">Archived</span> : null}<p>{[item.supplier.contactName, item.supplier.email, item.supplier.phone].filter(Boolean).join(' · ') || 'No contact details'}</p></div>;
      return <li key={item.id} className={onSelect ? undefined : 'po-supplier-directory-row'}>
        {onSelect ? <>{details}<button type="button" className="secondary" aria-label={`Select ${item.supplier.name}`} disabled={item.isArchived || pending} onClick={() => onSelect(item)}>Select</button></> : <a aria-label={`Edit ${item.supplier.name}`} href={`/suppliers/${item.id}`} onClick={follow}>{details}<Icon name="chevron" /></a>}
      </li>;
    })}</ul>
    {page?.nextCursor ? <button className="secondary" type="button" disabled={pending} onClick={() => void load(true)}>Load more suppliers</button> : null}
  </section>;
}
