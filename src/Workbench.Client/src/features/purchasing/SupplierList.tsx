import { useCallback, useEffect, useRef, useState, type MouseEvent } from 'react';
import { ApiError } from '../../api/auth';
import { getSuppliers, type Supplier, type SupplierPage } from '../../api/suppliers';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import './purchasing.css';
interface Props { follow?(event: MouseEvent<HTMLAnchorElement>): void; onSelect?(supplier: Supplier): void; onAuthLost(): void; }
export function SupplierList({ follow, onSelect, onAuthLost }: Props) {
  const [page, setPage] = useState<SupplierPage>();
  const latestPage = useRef<SupplierPage | undefined>(undefined);
  const [query, setQuery] = useState('');
  const [loadedQuery, setLoadedQuery] = useState('');
  const [archived, setArchived] = useState(false);
  const loadedFilter = useRef({ query: '', archived: false });
  const [pending, setPending] = useState(true);
  const [message, setMessage] = useState('');
  const sequence = useRef(0);
  const active = useRef(true);
  const load = useCallback(async (more = false, search = loadedFilter.current.query, includeArchived = loadedFilter.current.archived) => {
    const generation = ++sequence.current; setPending(true); setMessage('');
    try {
      const result = await getSuppliers(more ? latestPage.current?.nextCursor ?? undefined : undefined, search || undefined, includeArchived);
      if (!active.current || generation !== sequence.current) return;
      const known = new Set(latestPage.current?.items.map(item => item.id));
      const next = more ? { ...result, items: [...(latestPage.current?.items ?? []), ...result.items.filter(item => !known.has(item.id))] } : result;
      loadedFilter.current = { query: search, archived: includeArchived }; latestPage.current = next; setPage(next); setLoadedQuery(search);
    } catch (error) {
      if (!active.current || generation !== sequence.current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { latestPage.current = undefined; setPage(undefined); onAuthLost(); }
      else setMessage('Suppliers could not be loaded. Loaded suppliers are kept; retry your search or refresh.');
    } finally { if (active.current && generation === sequence.current) setPending(false); }
  }, [onAuthLost]);
  useEffect(() => { active.current = true; const requests = sequence; let mounted = true; queueMicrotask(() => { if (mounted) void load(); }); return () => { mounted = false; active.current = false; ++requests.current; }; }, [load]);
  return <section className="po-list" aria-label="Supplier directory">
    {!onSelect ? <>
    <a className="quiet button po-back" href="/purchase-orders" onClick={follow}><Icon name="back" />Back to purchase orders</a><header className="po-page-heading"><div><h1>Suppliers</h1><p className="lede">Your contacts for future purchases.</p></div><a href="/suppliers/new" onClick={follow} className="primary button"><Icon name="plus" />New supplier</a></header></> : null}
    <form className="po-search" onSubmit={event => { event.preventDefault(); event.stopPropagation(); void load(false, query.trim(), archived); }}>
      <div className="po-search-controls"><FloatingField htmlFor="supplier-search" label="Search suppliers"><input type="search" id="supplier-search" maxLength={200} value={query} onChange={event => setQuery(event.target.value)} placeholder="Supplier name" /></FloatingField><button className="secondary" type="submit">Search</button>
      <button type="button" className="quiet po-search-refresh" aria-label="Refresh suppliers" disabled={pending} onClick={() => void load(false, query.trim(), archived)}>Refresh</button>
      {query || loadedQuery ? <button className="quiet" type="button" onClick={() => { setQuery(''); void load(false, '', archived); }}>Clear search</button> : null}</div>
      {!onSelect ? <label className="po-archive-filter"><input type="checkbox" checked={archived} onChange={event => { setArchived(event.target.checked); void load(false, query.trim(), event.target.checked); }} />Include archived suppliers</label> : null}
    </form>
    {pending ? <p role="status">Loading suppliers…</p> : null}{message ? <p role="alert">{message}</p> : null}
    {page?.items.length === 0 ? <p className="po-empty-state">{loadedQuery ? 'No matching suppliers.' : 'No suppliers yet.'}</p> : null}
    <ul className="po-supplier-list">{page?.items.map(item => <li key={item.id}>
      <div><strong>{item.supplier.name}</strong>{item.isArchived ? <span className="po-badge">Archived</span> : null}
      <p>{[item.supplier.contactName, item.supplier.email, item.supplier.phone].filter(Boolean).join(' · ') || 'No contact details'}</p></div>
      {onSelect ? <button type="button" className="secondary" aria-label={`Select ${item.supplier.name}`} disabled={item.isArchived || pending} onClick={() => onSelect(item)}>Select</button> : <a className="quiet button" aria-label={`Edit ${item.supplier.name}`} href={`/suppliers/${item.id}`} onClick={follow}>Edit<Icon name="chevron" /></a>}
    </li>)}</ul>
    {page?.nextCursor ? <button className="secondary" type="button" disabled={pending} onClick={() => void load(true)}>Load more suppliers</button> : null}
  </section>;
}
