import { useCallback, useEffect, useRef, useState, type MouseEvent } from 'react';
import { browseSharedGems, getSharedGem, listGemDrafts, GemReferenceAdminApiError, type GemReferenceDetailResponse, type GemReferenceDraftSelection } from '../../api/gemReferenceAdmin';

type Follow = (event: MouseEvent<HTMLAnchorElement>) => void;
const library = '/service-admin/gem-reference';
type Page<T> = { rows: T[]; nextCursor: string | null };
type ReadSession = { readsSuspended?: boolean; sessionRevision?: number; onAuthLost?: () => void };
function authorizationLost(error: unknown) {
  return error instanceof GemReferenceAdminApiError && (error.status === 401 || error.status === 403);
}

// The server owns cursors. Request only the first page and explicit continuation pages.
function useCursorPage<T>(load: (cursor?: string, signal?: AbortSignal) => Promise<Page<T>>, { readsSuspended = false, sessionRevision = 0, onAuthLost }: ReadSession) {
  const [page, setPage] = useState<Page<T>>({ rows: [], nextCursor: null });
  const [pending, setPending] = useState(true);
  const [failed, setFailed] = useState(false);
  const generation = useRef(0);
  useEffect(() => {
    const current = ++generation.current;
    if (readsSuspended) return;
    const controller = new AbortController();
    void load(undefined, controller.signal).then((next) => {
      if (current === generation.current) { setPage(next); setPending(false); }
    }).catch((error: unknown) => {
      if (current !== generation.current) return;
      if (authorizationLost(error)) { ++generation.current; onAuthLost?.(); }
      else setFailed(true);
      setPending(false);
    });
    return () => { generation.current = current + 1; controller.abort(); };
  }, [load, readsSuspended, sessionRevision, onAuthLost]);
  async function request(cursor?: string) {
    if (readsSuspended) return;
    const current = generation.current;
    setPending(true);
    setFailed(false);
    try {
      const next = await load(cursor, new AbortController().signal);
      if (current === generation.current) setPage((previous) => ({ rows: cursor ? [...previous.rows, ...next.rows] : next.rows, nextCursor: next.nextCursor }));
    } catch (error: unknown) {
      if (current !== generation.current) return;
      if (authorizationLost(error)) { ++generation.current; setPending(false); onAuthLost?.(); }
      else setFailed(true);
    }
    finally { if (current === generation.current) setPending(false); }
  }
  return { ...page, pending, failed, retry: () => void request(page.rows.length ? page.nextCursor ?? undefined : undefined), more: () => void request(page.nextCursor ?? undefined) };
}

export function GemCatalog({ selected, onSelectionChange, follow, selectionLocked = false, ...session }: { selected: GemReferenceDraftSelection[]; onSelectionChange: (selected: GemReferenceDraftSelection[]) => void; follow: Follow; selectionLocked?: boolean } & ReadSession) {
  const [view, setView] = useState<'published' | 'drafts'>('published');
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  return <>
    <header className="gem-page-heading"><div><h1>Gem reference</h1><p>Maintain the shared reference library.</p></div><a className="button-link" href={`${library}/new`} onClick={follow}>New draft</a></header>
    <div className="gem-view-switch" aria-label="Library views">
      <button type="button" aria-pressed={view === 'published'} onClick={() => setView('published')}>Published catalog</button>
      <button type="button" aria-pressed={view === 'drafts'} onClick={() => setView('drafts')}>Drafts</button>
    </div>
    {view === 'published' ? <>
      <form className="gem-search" onSubmit={(event) => { event.preventDefault(); setQuery(search.trim()); }}>
        <label htmlFor="gem-search">Search shared gems</label>
        <div><input id="gem-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} /><button type="submit">Search</button></div>
      </form>
      <PublishedCatalog key={`${query}:${session.sessionRevision ?? 0}:${!!session.readsSuspended}`} query={query} follow={follow} {...session} />
    </> : <DraftCatalog key={`${session.sessionRevision ?? 0}:${!!session.readsSuspended}`} selected={selected} onSelectionChange={onSelectionChange} follow={follow} selectionLocked={selectionLocked} {...session} />}
  </>;
}

function PublishedCatalog({ query, follow, ...session }: { query: string; follow: Follow } & ReadSession) {
  const load = useCallback(async (cursor?: string, signal?: AbortSignal) => {
    const page = await browseSharedGems({ query: query || undefined, cursor }, signal);
    return { rows: page.entries, nextCursor: page.nextCursor };
  }, [query]);
  const page = useCursorPage(load, session);
  return <section aria-label="Published catalog">
    {page.failed ? <LoadFailure noun="catalog" retry={page.retry} /> : null}
    {page.pending ? <p role="status">Loading shared entries…</p> : null}
    {!page.pending && !page.failed && !page.rows.length ? <p>{query ? 'No shared entries match this search.' : 'No published entries yet.'}</p> : null}
    <ul className="gem-catalog-list">{page.rows.map((entry) => <li key={entry.id}>
      <div><a href={`${library}/entries/${entry.id}`} onClick={follow}>{entry.commonName}</a><p>{[entry.materialKind, entry.group, entry.species, entry.variety].filter(Boolean).join(' · ')}</p></div>
      <span className="gem-state">Published</span>
    </li>)}</ul>
    {page.nextCursor ? <button type="button" disabled={page.pending} onClick={page.more}>Load more entries</button> : null}
  </section>;
}

async function loadDraftPage(cursor?: string) {
  const page = await listGemDrafts(cursor);
  return { rows: page.drafts, nextCursor: page.nextCursor };
}

function DraftCatalog({ selected, onSelectionChange, follow, selectionLocked, ...session }: { selected: GemReferenceDraftSelection[]; onSelectionChange: (selected: GemReferenceDraftSelection[]) => void; follow: Follow; selectionLocked: boolean } & ReadSession) {
  const page = useCursorPage(loadDraftPage, session);
  return <section aria-label="Saved drafts">
    <div className="gem-review-bar"><p>{selected.length} of 50 drafts selected</p>{selected.length ? <a className="button-link" href={`${library}/review`} onClick={follow}>Review {selected.length} {selected.length === 1 ? 'draft' : 'drafts'}</a> : null}</div>
    {selected.length === 50 ? <p role="status">Selection limit reached. Clear a draft to select another.</p> : null}
    {page.failed ? <LoadFailure noun="drafts" retry={page.retry} /> : null}
    {page.pending ? <p role="status">Loading saved drafts…</p> : null}
    {!page.pending && !page.failed && !page.rows.length ? <p>No saved drafts yet. Start with a new draft or edit a published entry.</p> : null}
    <ul className="gem-catalog-list">{page.rows.map((draft) => {
      const checked = selected.some((item) => item.draftId === draft.id);
      return <li key={draft.id}>
        <label className="gem-draft-select"><input type="checkbox" aria-label={`Select ${draft.content.commonName || 'Untitled draft'}`} checked={checked} disabled={selectionLocked || (!checked && selected.length >= 50)} onChange={(event) => onSelectionChange(event.target.checked ? [...selected, { draftId: draft.id, expectedDraftRowVersion: draft.rowVersion }] : selected.filter((item) => item.draftId !== draft.id))} /></label>
        <div className="gem-row-content"><a href={`${library}/drafts/${draft.id}`} onClick={follow}>{draft.content.commonName || 'Untitled draft'}</a><p>{draft.content.isRetired ? 'Retirement draft' : draft.expectedPublishedRowVersion ? 'Changes to published entry' : 'New entry'} · Updated <time dateTime={draft.updatedAtUtc}>{new Date(draft.updatedAtUtc).toLocaleDateString()}</time></p></div>
        <span className="gem-state">{Object.keys(draft.errors).length ? 'Needs attention' : 'Draft'}</span>
      </li>;
    })}</ul>
    {page.nextCursor ? <button type="button" disabled={page.pending} onClick={page.more}>Load more drafts</button> : null}
  </section>;
}

function LoadFailure({ noun, retry }: { noun: string; retry: () => void }) {
  return <div className="form-message error"><p role="alert">We could not load the {noun}. Try again.</p><button type="button" onClick={retry}>Retry {noun}</button></div>;
}

function safeSourceUrl(url: string | null): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    return (parsed.protocol === 'https:' || parsed.protocol === 'http:') && !parsed.username && !parsed.password ? parsed.href : null;
  } catch { return null; }
}

export function GemDetail({ entryId, follow, readsSuspended = false, sessionRevision = 0, onAuthLost }: { entryId: string; follow: Follow } & ReadSession) {
  return <GemDetailContent key={`${entryId}:${sessionRevision}:${readsSuspended}`} entryId={entryId} follow={follow} readsSuspended={readsSuspended} sessionRevision={sessionRevision} onAuthLost={onAuthLost} />;
}

function GemDetailContent({ entryId, follow, readsSuspended = false, sessionRevision = 0, onAuthLost }: { entryId: string; follow: Follow } & ReadSession) {
  const [entry, setEntry] = useState<GemReferenceDetailResponse | null>(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const drafts = useCursorPage(loadDraftPage, { readsSuspended, sessionRevision, onAuthLost });
  useEffect(() => {
    let current = true;
    if (readsSuspended) return;
    void getSharedGem(entryId).then((detail) => { if (current) setEntry(detail); })
      .catch((error: unknown) => {
        if (!current) return;
        if (authorizationLost(error)) onAuthLost?.();
        else setFailed(true);
      });
    return () => { current = false; };
  }, [entryId, attempt, readsSuspended, sessionRevision, onAuthLost]);
  return <>
    <a className="gem-back" href={library} onClick={follow}>Back to gem reference</a>
    {failed ? <LoadFailure noun="entry" retry={() => { setFailed(false); setAttempt((value) => value + 1); }} /> : null}
    {!entry && !failed ? <p role="status">Loading shared entry…</p> : null}
    {entry ? <>
      <header className="gem-page-heading"><div><h1>{entry.commonName}</h1><p className="gem-state">{entry.retirement.isRetired ? 'Retired' : 'Published'}</p></div><a className="button-link" href={`${library}/entries/${entry.id}/edit`} onClick={follow}>Edit entry</a></header>
      {entry.retirement.isRetired ? <section className="gem-retirement" aria-label="Retirement"><p>{entry.retirement.explanation}</p>{entry.retirement.redirectEntryId ? <a href={`${library}/entries/${entry.retirement.redirectEntryId}`} onClick={follow}>View replacement entry</a> : null}</section> : null}
      <dl className="gem-taxonomy">{([['Material kind', entry.materialKind], ['Group', entry.group], ['Species', entry.species], ['Variety', entry.variety]] as const).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value || 'Not specified'}</dd></div>)}</dl>
      <section className="gem-detail-section"><h2>Description</h2><p>{entry.description || 'No description provided.'}</p></section>
      <section className="gem-detail-section"><h2>Aliases</h2>{entry.aliases.length ? <ul>{entry.aliases.map((alias) => <li key={alias}>{alias}</li>)}</ul> : <p>No aliases recorded.</p>}</section>
      {entry.notableLocality ? <section className="gem-detail-section"><h2>Notable locality</h2><p>{entry.notableLocality.place}</p><p>{entry.notableLocality.scope}</p><p>Reviewed <time dateTime={entry.notableLocality.reviewedOn}>{entry.notableLocality.reviewedOn}</time></p><a href={`#source-${entry.notableLocality.sourceAssertionId}`}>Supporting source</a></section> : null}
      <section className="gem-detail-section"><h2>Sources</h2>{!entry.sourceAssertions.length ? <p>No sources recorded.</p> : <ul className="gem-source-list">{entry.sourceAssertions.map((source) => {
        const url = safeSourceUrl(source.url);
        return <li key={source.id} id={`source-${source.id}`}><h3>{url ? <a href={url} target="_blank" rel="noopener noreferrer">{source.title}</a> : source.title}</h3><p>{source.field} · {source.publisher}</p>{source.citation ? <p>{source.citation}</p> : null}<p>Reviewed <time dateTime={source.reviewedOn}>{source.reviewedOn}</time>{source.accessedOn ? <> · Accessed <time dateTime={source.accessedOn}>{source.accessedOn}</time></> : null}</p></li>;
      })}</ul>}</section>
      <section className="gem-detail-section"><h2>Saved drafts for this entry</h2>
        {drafts.failed ? <LoadFailure noun="drafts" retry={drafts.retry} /> : null}
        {drafts.pending ? <p role="status">Checking saved drafts…</p> : null}
        <ul>{drafts.rows.filter((draft) => draft.entryId === entryId).map((draft) => <li key={draft.id}><a href={`${library}/drafts/${draft.id}`} onClick={follow}>Open {draft.content.commonName || 'Untitled draft'}</a></li>)}</ul>
        {!drafts.pending && !drafts.failed && !drafts.rows.some((draft) => draft.entryId === entryId) ? <p>{drafts.nextCursor ? 'No matching drafts on the loaded pages.' : 'No saved drafts found for this entry.'}</p> : null}
        {drafts.nextCursor ? <button type="button" disabled={drafts.pending} onClick={drafts.more}>Check more drafts</button> : null}
      </section>
    </> : null}
  </>;
}
