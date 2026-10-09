import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { browseGems, GemReferenceApiError, type GemReferencePageResponse } from '../../api/gemReference';
import { GemLibraryMemory, type LibraryFilters } from './gemLibraryMemory';
import { gemPath, layerLabel, materialKinds, type GemNavigation } from './gemPresentation';
import './gem-reference.css';

export function GemLibrary({ memory, follow, onAuthLost }: GemNavigation & { memory: GemLibraryMemory }) {
  const [draft, setDraft] = useState<LibraryFilters>(() => memory.snapshot?.draft ?? {});
  const [filters, setFilters] = useState<LibraryFilters>(() => memory.snapshot?.filters ?? {});
  const [page, setPage] = useState<GemReferencePageResponse | undefined>(() => memory.snapshot?.page);
  const [request, setRequest] = useState<{ cursor?: string } | undefined>(() => memory.snapshot?.page ? undefined : {});
  const [failure, setFailure] = useState<{ cursor?: string; message: string }>();
  const ended = useRef(false);
  const [completion, setCompletion] = useState<{ added: number; continued: boolean }>();
  const heading = useRef<HTMLHeadingElement>(null);
  const list = useRef<HTMLUListElement>(null);
  useLayoutEffect(() => {
    const selected = memory.selected && list.current?.querySelector<HTMLAnchorElement>(`a[href="${memory.selected}"]`);
    (selected || heading.current)?.focus({ preventScroll: true });
    window.scrollTo({ top: memory.scrollY, behavior: 'instant' });
    const save = () => { memory.savePosition(window.scrollY); };
    window.addEventListener('scroll', save, { passive: true });
    return () => window.removeEventListener('scroll', save);
  }, [memory]);
  useLayoutEffect(() => { if (!ended.current) memory.save({ draft, filters, page }); }, [memory, draft, filters, page]);
  useEffect(() => {
    if (!request || ended.current) return;
    const controller = new AbortController();
    let current = true;
    void browseGems({ ...filters, ...request }, controller.signal).then(result => {
      if (!current) return;
      setPage(previous => request.cursor && previous ? { entries: [...previous.entries, ...result.entries], nextCursor: result.nextCursor } : result);
      setCompletion({ added: result.entries.length, continued: !!request.cursor });
      setRequest(undefined);
      setFailure(undefined);
    }).catch((error: unknown) => {
      if (!current) return;
      if (error instanceof GemReferenceApiError && (error.status === 401 || error.status === 403)) {
        ended.current = true;
        memory.clear();
        onAuthLost();
        return;
      }
      const problem = error instanceof GemReferenceApiError && error.status === 400 ? error.problem : undefined;
      const message = problem && typeof problem === 'object' && 'title' in problem && typeof problem.title === 'string'
        ? problem.title : 'We could not load the gem reference. Try again.';
      setFailure({ ...request, message });
      setRequest(undefined);
    });
    return () => { current = false; controller.abort(); };
  }, [request, filters, memory, onAuthLost]);
  function search(next: LibraryFilters) {
    const cleaned = Object.fromEntries(Object.entries(next).map(([key, value]) => [key, value?.trim()]).filter(([, value]) => value));
    setFilters(cleaned);
    setPage(undefined);
    setCompletion(undefined);
    setFailure(undefined);
    memory.select(undefined);
    memory.savePosition(0);
    setRequest({});
  }
  return <section className="reference-page">
    <header className="page-heading"><div><h1 ref={heading} tabIndex={-1}>Gem reference</h1><p className="lede">Explore the shared library and your tenant’s effective reference.</p></div></header>
    <form role="search" aria-label="Gem reference" className="reference-search" onSubmit={event => { event.preventDefault(); search(draft); }}>
      <label className="reference-query">Search gems<input type="search" maxLength={200} value={draft.query ?? ''} onChange={event => setDraft({ ...draft, query: event.target.value })} placeholder="Name, alias or classification" /></label>
      <div className="reference-filter"><label htmlFor="reference-material-kind">Material kind</label><select id="reference-material-kind" value={draft.materialKind ?? ''} onChange={event => setDraft({ ...draft, materialKind: event.target.value })}><option value="">All materials</option>{Object.entries(materialKinds).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
      <label>Group (exact match)<input maxLength={200} value={draft.group ?? ''} onChange={event => setDraft({ ...draft, group: event.target.value })} /></label>
      <div className="button-row"><button type="submit">Search</button><button className="secondary" type="button" onClick={() => { setDraft({}); search({}); }}>Clear filters</button></div>
    </form>
    {failure ? <div className="form-message error"><p role="alert">{failure.message}</p><button type="button" onClick={() => { setRequest(failure.cursor ? { cursor: failure.cursor } : {}); setFailure(undefined); }}>Retry</button></div> : null}
    <p role="status" aria-live="polite" aria-atomic="true">{request ? 'Loading gem entries…' : failure || !page ? '' : page.entries.length === 0 ? (Object.keys(filters).length ? 'No gems match these filters.' : 'No gem entries are available yet.') : `${completion?.continued ? `${completion.added} ${completion.added === 1 ? 'entry' : 'entries'} added; ` : ''}${page.entries.length} ${page.entries.length === 1 ? 'entry' : 'entries'} shown.`}</p>
    <ul ref={list} className="reference-results" aria-label="Gem entries">{page?.entries.map(entry => {
      const href = gemPath(entry.id, entry.origin);
      return <li key={href}><div><a href={href} onClick={event => { if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) memory.select(href); follow(event); }}>{entry.commonName}</a>
        {entry.needsReview ? <p className="reference-warning">Needs review · classification unavailable</p> : <p>{[materialKinds[entry.materialKind as keyof typeof materialKinds] ?? entry.materialKind, entry.group, entry.species, entry.variety].filter(Boolean).join(' · ')}</p>}</div><span className="reference-layer">{layerLabel(entry.layer)}</span></li>;
    })}</ul>
    {page?.nextCursor ? <button type="button" disabled={!!request || !!failure} onClick={() => setRequest({ cursor: page.nextCursor! })}>Load more entries</button> : null}
  </section>;
}
