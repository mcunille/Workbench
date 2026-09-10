import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { findAcquisitions, type Acquisition, type AcquisitionPage } from '../../api/acquisitions';
import { acquisitionLabel } from './acquisitionDraft';

export function AcquisitionPicker({ onSelect, onCancel, onAuthLost }: {
  onSelect(value: Acquisition): void;
  onCancel(): void;
  onAuthLost(): void;
}) {
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState({ search: '', attempt: 0 });
  const [page, setPage] = useState<AcquisitionPage>();
  const [failed, setFailed] = useState(false);
  const [pending, setPending] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const generation = useRef(0);
  useEffect(() => { input.current?.focus(); }, []);
  useEffect(() => {
    const request = ++generation.current;
    void findAcquisitions(query.search).then(result => {
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
      const next = await findAcquisitions(query.search, page.nextCursor);
      if (request === generation.current) setPage({ items: [...page.items, ...next.items], nextCursor: next.nextCursor });
    } catch (error) {
      if (request !== generation.current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      else setFailed(true);
    } finally { if (request === generation.current) setPending(false); }
  }
  return <section aria-labelledby="acquisition-picker-title">
    <h3 id="acquisition-picker-title">Choose an acquisition</h3>
    <p>Search saved source and notes. Acquisitions with no connected pieces are included.</p>
    <form className="button-row" onSubmit={event => {
      event.preventDefault(); setPage(undefined); setFailed(false); setPending(true);
      setQuery(previous => ({ search: search.trim(), attempt: previous.attempt + 1 }));
    }}>
      <label htmlFor="acquisition-search">Search acquisitions</label>
      <input ref={input} id="acquisition-search" type="search" maxLength={200} value={search} onChange={event => setSearch(event.target.value)} />
      <button className="secondary" type="submit" disabled={pending}>Search</button>
    </form>
    {!page && !failed || pending ? <p role="status">Loading acquisitions…</p> : null}
    {failed ? <div role="alert"><p>We could not load acquisitions.</p><button className="secondary" disabled={pending} onClick={() => {
      setFailed(false); setPending(true);
      if (page) void more(); else setQuery(previous => ({ ...previous, attempt: previous.attempt + 1 }));
    }}>Retry loading acquisitions</button></div> : null}
    {page?.items.length === 0 ? <p>No acquisitions found.</p> : null}
    <ul className="acquisition-picker-list">{page?.items.map(value => <li key={value.id}>
      <button className="secondary" onClick={() => onSelect(value)}>Select {acquisitionLabel(value)}</button>
      {value.notes ? <p className="hint">{value.notes}</p> : null}
    </li>)}</ul>
    <div className="button-row">
      {page?.nextCursor ? <button className="secondary" disabled={pending} onClick={() => void more()}>Load more acquisitions</button> : null}
      <button className="secondary" onClick={onCancel}>Cancel</button>
    </div>
  </section>;
}
