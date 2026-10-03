import { useEffect, useRef, useState } from 'react';
import { getGemDraft, getSharedGem, saveGemDraft, type GemReferenceContent, type GemReferenceDetailResponse, type GemReferenceDraftResponse } from '../../api/gemReferenceAdmin';
import { GemSourceFields } from './GemSourceFields';

type EditorProps = { entryId?: string; draftId?: string; onDirtyChange: (dirty: boolean, uncertain?: boolean) => void; onAuthLost: () => void; onSaved: (draft: GemReferenceDraftResponse) => void; writesSuspended?: boolean; sessionRevision?: number };
type CurrentVersions = { draft: GemReferenceDraftResponse | null; published: GemReferenceDetailResponse | null };
type FieldErrors = Record<string, string[]>;
function statusOf(error: unknown): number | undefined {
  return typeof error === 'object' && error !== null && 'status' in error && typeof error.status === 'number' ? error.status : undefined;
}
function fieldErrors(error: unknown): FieldErrors {
  if (typeof error !== 'object' || error === null || !('problem' in error) || typeof error.problem !== 'object' || error.problem === null || !('errors' in error.problem) || typeof error.problem.errors !== 'object' || error.problem.errors === null) return {};
  return Object.fromEntries(Object.entries(error.problem.errors).filter((entry): entry is [string, string[]] => Array.isArray(entry[1]) && entry[1].every((value) => typeof value === 'string')));
}
function fromPublished(entry: GemReferenceDetailResponse): GemReferenceContent {
  return { id: entry.id, materialKind: entry.materialKind, commonName: entry.commonName, group: entry.group, species: entry.species, variety: entry.variety, description: entry.description, aliases: entry.aliases, sources: entry.sourceAssertions.map(({ id, field, title, publisher, url, citation, accessedOn, reviewedOn }) => ({ id, field, title, publisher, url, citation, accessedOn, reviewedOn })), notableLocality: entry.notableLocality, isRetired: entry.retirement.isRetired, retirementExplanation: entry.retirement.explanation, redirectEntryId: entry.retirement.redirectEntryId };
}
function blankContent(): GemReferenceContent {
  return { id: crypto.randomUUID(), materialKind: 'mineral', commonName: '', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null };
}
async function optional<T>(request: Promise<T>): Promise<T | null> {
  try { return await request; } catch (error) { if (statusOf(error) === 404) return null; throw error; }
}
function CurrentContent({ content }: { content: GemReferenceContent }) {
  return <dl className="gem-current-content">
    {([['Common name', content.commonName], ['Material kind', content.materialKind], ['Group', content.group], ['Species', content.species], ['Variety', content.variety], ['Description', content.description], ['Aliases', content.aliases.join(', ')], ['Retirement', content.isRetired ? 'Retired' : 'Active'], ['Retirement explanation', content.retirementExplanation], ['Redirect entry ID', content.redirectEntryId]] as const).map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{value || 'None'}</dd></div>)}
    <div><dt>Sources</dt><dd>{content.sources.length ? <ul>{content.sources.map((source) => <li key={source.id}>{source.field}: {source.title || 'Untitled'} — {source.publisher || 'No publisher'}; URL {source.url || 'None'}; citation {source.citation || 'None'}; accessed {source.accessedOn || 'None'}; reviewed {source.reviewedOn}; ID {source.id}</li>)}</ul> : 'None'}</dd></div>
    <div><dt>Locality</dt><dd>{content.notableLocality ? `${content.notableLocality.place}; ${content.notableLocality.scope}; reviewed ${content.notableLocality.reviewedOn}; source ${content.notableLocality.sourceAssertionId}` : 'None'}</dd></div>
  </dl>;
}

export function GemEditor({ entryId, draftId, onDirtyChange, onAuthLost, onSaved, writesSuspended = false, sessionRevision = 0 }: EditorProps) {
  // Adopting a confirmed draft URL must not replace the editor's in-memory identity.
  const [origin] = useState(() => ({ entryId, draftId: draftId ?? crypto.randomUUID(), existingDraft: !!draftId }));
  const [content, setContent] = useState<GemReferenceContent | null>(() => draftId || entryId ? null : blankContent());
  const [baseline, setBaseline] = useState(() => content ? JSON.stringify(content) : '');
  const [draftVersion, setDraftVersion] = useState<string | null>(null);
  const [publishedVersion, setPublishedVersion] = useState<string | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [message, setMessage] = useState('');
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [reconciliationPending, setReconciliationPending] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const [sessionLost, setSessionLost] = useState(false);
  const [refreshRequired, setRefreshRequired] = useState(false);
  const [current, setCurrent] = useState<CurrentVersions | null>(null);
  const summary = useRef<HTMLDivElement>(null);
  const mounted = useRef(true);
  const previousSession = useRef(sessionRevision);
  const dirty = reconciliationPending || (!!content && JSON.stringify(content) !== baseline);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { onDirtyChange(dirty || uncertain || busy, uncertain || busy); }, [dirty, uncertain, busy, onDirtyChange]);
  useEffect(() => { if (message || Object.keys(errors).length) summary.current?.focus(); }, [message, errors]);
  useEffect(() => {
    if (!origin.existingDraft && !origin.entryId) return;
    let active = true;
    const request = origin.existingDraft ? getGemDraft(origin.draftId) : getSharedGem(origin.entryId!);
    void request.then((value) => {
      if (!active) return;
      const isDraft = 'content' in value;
      const next = isDraft ? value.content : fromPublished(value);
      setContent(next);
      setBaseline(JSON.stringify(next));
      setDraftVersion(isDraft ? value.rowVersion : null);
      setPublishedVersion(isDraft ? value.expectedPublishedRowVersion : value.rowVersion);
      setSaved(isDraft);
      setErrors(isDraft ? value.errors : {});
      setMessage('');
    }).catch((error: unknown) => {
      if (!active) return;
      if (statusOf(error) === 401 || statusOf(error) === 403) { setSessionLost(true); onAuthLost(); }
      setMessage(statusOf(error) === 404 ? 'This reference was not found. Return to the library and choose another draft.' : 'We could not load this draft. Retry when the service is available.');
    });
    return () => { active = false; };
  }, [origin, loadAttempt, onAuthLost]);

  async function refreshVersions() {
    if (!content) { setLoadAttempt((value) => value + 1); setSessionLost(false); return; }
    setBusy(true);
    setRefreshRequired(true);
    setCurrent(null);
    try {
      const [latestDraft, published] = await Promise.all([optional(getGemDraft(origin.draftId)), optional(getSharedGem(content.id))]);
      if (!mounted.current) return;
      setSessionLost(false);
      if ((latestDraft?.rowVersion ?? null) === draftVersion && (published?.rowVersion ?? null) === publishedVersion) {
        setRefreshRequired(false);
        setMessage('');
      } else {
        setCurrent({ draft: latestDraft, published });
        setMessage('The saved draft or published reference changed. Compare the current content with your edits, then choose how to continue.');
      }
    } catch (error) {
      if (!mounted.current) return;
      if (statusOf(error) === 401 || statusOf(error) === 403) { setSessionLost(true); onAuthLost(); }
      setMessage('We could not refresh the current versions. Your edits are retained; retry before saving.');
    } finally { if (mounted.current) setBusy(false); }
  }
  // A new authenticated session must recheck both concurrency baselines before writing.
  const refreshRef = useRef(refreshVersions);
  useEffect(() => { refreshRef.current = refreshVersions; });
  useEffect(() => {
    if (previousSession.current === sessionRevision) return;
    previousSession.current = sessionRevision;
    void refreshRef.current();
  }, [sessionRevision]);

  async function save() {
    if (!content || busy || sessionLost || writesSuspended || refreshRequired) return;
    setBusy(true);
    setMessage('');
    setErrors({});
    try {
      const result = await saveGemDraft(origin.draftId, { entryId: content.id, content, expectedDraftRowVersion: draftVersion, expectedPublishedRowVersion: publishedVersion });
      if (!mounted.current) return;
      setContent(result.content);
      setBaseline(JSON.stringify(result.content));
      setDraftVersion(result.rowVersion);
      setPublishedVersion(result.expectedPublishedRowVersion);
      setErrors(result.errors);
      setSaved(true);
      setReconciliationPending(false);
      setUncertain(false);
      onSaved(result);
    } catch (error) {
      if (!mounted.current) return;
      const status = statusOf(error);
      setErrors(fieldErrors(error));
      if (status === 401 || status === 403) {
        setSessionLost(true);
        onAuthLost();
        setMessage('Your service-admin session ended. Sign in again to save; your edits remain here.');
      } else if (status === 409) {
        await refreshVersions();
      } else {
        setUncertain(status === undefined);
        setMessage(status === undefined ? 'We could not confirm this save. Your edits are retained. Retry to check the saved version before making another change.' : 'We could not save this draft. Review the errors and try again.');
      }
    } finally { if (mounted.current) setBusy(false); }
  }
  function update(next: GemReferenceContent) { setContent(next); setMessage(''); }
  function reconcile(useSaved: boolean) {
    if (!current) return;
    const next = useSaved ? current.draft?.content : content;
    if (!next) return;
    setContent(next);
    setDraftVersion(current.draft?.rowVersion ?? null);
    setPublishedVersion(useSaved ? current.draft?.expectedPublishedRowVersion ?? null : current.published?.rowVersion ?? null);
    if (useSaved) { setBaseline(JSON.stringify(next)); setErrors(current.draft?.errors ?? {}); }
    setReconciliationPending(!useSaved);
    setUncertain(false);
    setRefreshRequired(false);
    setCurrent(null);
    setMessage('');
  }

  const errorEntries = Object.entries(errors);
  if (!content) return <section><h1>Gem draft</h1>{message ? <><div ref={summary} role="alert" tabIndex={-1}>{message}</div><button type="button" disabled={writesSuspended || sessionLost} onClick={() => setLoadAttempt((value) => value + 1)}>Retry draft</button></> : <p role="status">Loading draft…</p>}</section>;
  return <>
    <header className="gem-page-heading"><div><h1>{origin.entryId ? 'Edit shared entry' : 'Gem draft'}</h1><p>Save your work as a shared draft. Publication is a separate review step.</p></div><span className="gem-state">{dirty ? 'Unsaved changes' : saved ? 'Saved draft' : 'New draft'}</span></header>
    {message || errorEntries.length ? <div ref={summary} className="form-message error gem-error-summary" role="alert" tabIndex={-1}>
      {message ? <p>{message}</p> : <p>{dirty ? 'Validation issues from the last saved draft. Save your corrections to check them again.' : 'Draft saved with issues to resolve before publication.'}</p>}
      {errorEntries.length ? <ul>{errorEntries.map(([field, values]) => <li key={field}><a href={`#gem-${field === 'retirement' ? 'retirement' : field === 'sources' ? 'sources' : field === 'notableLocality' ? 'locality' : 'classification'}`}>{field}: {values.join(' ')}</a></li>)}</ul> : null}
    </div> : null}
    {sessionLost || writesSuspended ? <p role="status">Saving is paused until service-admin sign-in and version refresh finish.</p> : null}
    <div className={current ? 'gem-editor-layout gem-editor-conflicted' : 'gem-editor-layout'}>
      <form className="gem-editor" onSubmit={(event) => { event.preventDefault(); void save(); }} noValidate>
        <fieldset className="gem-editor-controls" disabled={busy}>
          <section id="gem-classification" className="gem-editor-section" aria-labelledby="classification-heading">
            <h2 id="classification-heading">{current ? 'Your edits' : 'Classification'}</h2>
            <div className="gem-form-grid">
              <label>Common name<input maxLength={200} value={content.commonName} onChange={(event) => update({ ...content, commonName: event.target.value })} /></label>
              <label>Material kind<select value={content.materialKind} onChange={(event) => update({ ...content, materialKind: event.target.value })}><option value="mineral">Mineral</option><option value="mineraloid">Mineraloid</option><option value="organic">Organic</option><option value="rockAggregate">Rock aggregate</option></select></label>
              {(['group', 'species', 'variety'] as const).map((field) => <label key={field}>{field.charAt(0).toUpperCase() + field.slice(1)}<input maxLength={200} value={content[field] ?? ''} onChange={(event) => update({ ...content, [field]: event.target.value || null })} /></label>)}
              <label className="gem-wide-field">Description<textarea rows={4} maxLength={2000} value={content.description ?? ''} onChange={(event) => update({ ...content, description: event.target.value || null })} /></label>
            </div>
          </section>
          <section className="gem-editor-section" aria-labelledby="aliases-heading"><h2 id="aliases-heading">Aliases</h2>
            {content.aliases.map((alias, index) => <div className="gem-alias-row" key={index}><label>Alias {index + 1}<input maxLength={200} value={alias} onChange={(event) => update({ ...content, aliases: content.aliases.map((value, position) => position === index ? event.target.value : value) })} /></label><button type="button" className="secondary" onClick={() => update({ ...content, aliases: content.aliases.filter((_value, position) => position !== index) })}>Remove alias {index + 1}</button></div>)}
            <button type="button" className="secondary" disabled={content.aliases.length >= 20} onClick={() => update({ ...content, aliases: [...content.aliases, ''] })}>Add alias</button><p className="gem-state">{content.aliases.length} of 20 aliases</p>
          </section>
          <div id="gem-sources"><GemSourceFields content={content} onChange={update} /></div>
          <section id="gem-retirement" className="gem-editor-section" aria-labelledby="retirement-heading"><h2 id="retirement-heading">Retirement</h2>
            <label className="gem-check"><input type="checkbox" checked={content.isRetired} onChange={(event) => update({ ...content, isRetired: event.target.checked, retirementExplanation: event.target.checked ? content.retirementExplanation : null, redirectEntryId: event.target.checked ? content.redirectEntryId : null })} />Retire entry</label>
            {content.isRetired ? <><p>Give an explanation or the ID of a replacement shared entry. Retirement takes effect after publication.</p><div className="gem-form-grid"><label className="gem-wide-field">Retirement explanation<textarea rows={3} maxLength={2000} value={content.retirementExplanation ?? ''} onChange={(event) => update({ ...content, retirementExplanation: event.target.value || null })} /></label><label>Redirect entry ID<input value={content.redirectEntryId ?? ''} onChange={(event) => update({ ...content, redirectEntryId: event.target.value || null })} /></label></div></> : null}
          </section>
        </fieldset>
        <div className="gem-editor-actions"><button className="primary" type="submit" disabled={busy || sessionLost || writesSuspended || refreshRequired}>{busy ? 'Checking draft…' : 'Save draft'}</button>{saved && !dirty && !uncertain ? <p role="status">Draft saved. Publication requires review.</p> : null}</div>
      </form>
      {current ? <section className="gem-conflict" aria-labelledby="conflict-heading"><h2 id="conflict-heading">Resolve changed versions</h2><p>Compare these current versions with your edits. Nothing has been replaced automatically.</p>
        {current.draft ? <><h3>Current saved draft</h3><CurrentContent content={current.draft.content} /></> : <p>No saved draft exists yet.</p>}
        {current.published ? <><h3>Current published reference</h3><CurrentContent content={fromPublished(current.published)} /></> : <p>No published reference exists yet.</p>}
        <div className="button-row"><button type="button" disabled={busy || sessionLost || writesSuspended} onClick={() => reconcile(false)}>Keep my edits with current versions</button>{current.draft ? <button type="button" className="secondary" disabled={busy || sessionLost || writesSuspended} onClick={() => reconcile(true)}>Use current saved draft</button> : null}</div>
      </section> : null}
    </div>
    {refreshRequired && !current && !busy && !sessionLost && !writesSuspended ? <button type="button" onClick={() => void refreshVersions()}>Retry version refresh</button> : null}
  </>;
}
