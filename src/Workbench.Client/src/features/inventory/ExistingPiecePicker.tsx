import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getAcquisition, type AcquisitionContext } from '../../api/acquisitions';
import { getItem, getItems, type ItemDetail, type ItemPage } from '../../api/items';
import { acquisitionLabel } from './acquisitionDraft';

type Page = { items: { item: ItemPage['items'][number]; context: AcquisitionContext }[]; nextCursor?: string | null };
export function ExistingPiecePicker({ onSelect, onCancel, onAuthLost }: {
  onSelect(item: ItemDetail, context: AcquisitionContext): void;
  onCancel(): void;
  onAuthLost(): void;
}) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState({ search: '', attempt: 0 });
  const [page, setPage] = useState<Page>();
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);
  const generation = useRef(0);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { input.current?.focus(); }, []);
  async function load(search: string, cursor?: string) {
    const result = await getItems(cursor, search);
    const items = await Promise.all(result.items.map(async item => ({ item, context: await getAcquisition(item.id) })));
    return { items, nextCursor: result.nextCursor };
  }
  function fail(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
    else setFailed(true);
  }
  useEffect(() => {
    const request = ++generation.current;
    void load(query.search).then(result => {
      if (generation.current === request) { setPage(result); setPending(false); }
    }, error => {
      if (generation.current !== request) return;
      setPending(false);
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      else setFailed(true);
    });
    return () => { generation.current = request + 1; };
  }, [query, onAuthLost]);
  async function more() {
    if (pending || !page?.nextCursor) return;
    const request = generation.current;
    setPending(true); setFailed(false);
    try {
      const next = await load(query.search, page.nextCursor);
      if (generation.current === request) setPage({ items: [...page.items, ...next.items], nextCursor: next.nextCursor });
    } catch (error) { if (generation.current === request) fail(error); }
    finally { if (generation.current === request) setPending(false); }
  }
  async function select(id: string) {
    if (pending) return;
    const request = generation.current;
    setPending(true); setFailed(false);
    try {
      const [item, context] = await Promise.all([getItem(id), getAcquisition(id)]);
      if (generation.current !== request) return;
      if (item.archivedAtUtc) setFailed(true);
      else onSelect(item, context);
    } catch (error) { if (generation.current === request) fail(error); }
    finally { if (generation.current === request) setPending(false); }
  }
  return <section aria-labelledby="piece-picker-title">
    <h2 id="piece-picker-title">Connect existing piece</h2>
    <form className="button-row" onSubmit={event => {
      event.preventDefault(); setPage(undefined); setFailed(false); setPending(true);
      setQuery(previous => ({ search: search.trim(), attempt: previous.attempt + 1 }));
    }}>
      <label htmlFor="piece-search">Search active pieces</label>
      <input ref={input} id="piece-search" type="search" value={search} maxLength={200} onChange={event => setSearch(event.target.value)} />
      <button className="secondary" type="submit" disabled={pending}>Search pieces</button>
    </form>
    {!page && !failed || pending ? <p role="status">Loading active pieces and current connections…</p> : null}
    {failed ? <div role="alert"><p>We could not load active pieces with their current connections. Try loading again.</p>
      <button className="secondary" disabled={pending} onClick={() => { setPage(undefined); setFailed(false); setPending(true); setQuery(previous => ({ ...previous, attempt: previous.attempt + 1 })); }}>Retry loading pieces</button></div> : null}
    {page?.items.length === 0 ? <p>No active pieces found.</p> : null}
    <ul className="acquisition-picker-list">{page?.items.map(({ item, context }) => <li key={item.id}>
      <button className="secondary" disabled={pending} onClick={() => void select(item.id)}>Select {item.name}</button>
      <p className="hint">Current acquisition: {context.acquisition ? acquisitionLabel(context.acquisition) : 'None'}</p>
    </li>)}</ul>
    <div className="button-row">{page?.nextCursor ? <button className="secondary" disabled={pending} onClick={() => void more()}>Load more pieces</button> : null}
      <button className="secondary" onClick={onCancel}>Cancel</button></div>
  </section>;
}
