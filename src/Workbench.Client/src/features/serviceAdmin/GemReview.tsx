import { useEffect, useRef, useState, type ReactNode } from 'react';
import { GemReferenceAdminApiError, getGemPublication, publishGemDrafts, reviewGemDrafts, type GemReferenceDraftSelection, type GemReferencePublishOutcome, type GemReferencePublishRequest, type GemReferenceReviewResponse } from '../../api/gemReferenceAdmin';
import { clearPendingPublication, readPendingPublication, writePendingPublication } from './publicationAttempt';
export type GemReviewProps = {
  accountId: string; selection: GemReferenceDraftSelection[];
  onPublished: (outcome: GemReferencePublishOutcome) => void; onEdit: (draftId: string) => void;
  writesSuspended?: boolean; sessionRevision?: number; onAuthLost: () => void;
  onPendingChange?: (pending: boolean) => void; onDirtyChange?: (dirty: boolean, uncertain?: boolean) => void;
};
type Receipt = { request: GemReferencePublishRequest | null; storageFailed: boolean };
function recover(accountId: string): Receipt {
  try { return { request: readPendingPublication(accountId), storageFailed: false }; }
  catch { return { request: null, storageFailed: true }; }
}
const fieldNames: Record<string, string> = { commonName: 'Common name', materialKind: 'Material kind', group: 'Group', species: 'Species', variety: 'Variety', description: 'Description', aliases: 'Aliases', sources: 'Sources', notableLocality: 'Notable locality', isRetired: 'Retired', retirementExplanation: 'Retirement explanation', redirectEntryId: 'Replacement entry', field: 'Claim', title: 'Title', publisher: 'Publisher', url: 'URL', citation: 'Citation', reviewedOn: 'Reviewed', accessedOn: 'Accessed', sourceAssertionId: 'Supporting source', place: 'Place', scope: 'Scope', id: 'ID' };
function ReviewValue({ value }: { value: unknown }): ReactNode {
  if (value === null || value === undefined || value === '') return <span className="gem-state">Not specified</span>;
  if (Array.isArray(value)) return value.length ? <ul>{value.map((item, index) => <li key={index}><ReviewValue value={item} /></li>)}</ul> : <span className="gem-state">None</span>;
  if (typeof value === 'object') return <dl className="gem-review-object">{Object.entries(value).map(([name, item]) => <div key={name}><dt>{fieldNames[name] ?? name}</dt><dd><ReviewValue value={item} /></dd></div>)}</dl>;
  return <span>{typeof value === 'boolean' ? value ? 'Yes' : 'No' : String(value)}</span>;
}

export function GemReview({ accountId, selection, onPublished, onEdit, writesSuspended = false, sessionRevision = 0, onAuthLost, onPendingChange, onDirtyChange }: GemReviewProps) {
  const [receipt, setReceipt] = useState(() => recover(accountId));
  const [reviewed, setReviewed] = useState<{ key: string; revision: number; review: GemReferenceReviewResponse } | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [terminal, setTerminal] = useState<GemReferencePublishOutcome | null>(null);
  const [reviewAttempt, setReviewAttempt] = useState(0);
  const active = useRef(false);
  const inFlight = useRef(false);
  const errorSummary = useRef<HTMLDivElement>(null);
  const selectionKey = JSON.stringify(selection);
  const currentScope = useRef({ accountId, selectionKey, sessionRevision, writesSuspended });
  useEffect(() => { currentScope.current = { accountId, selectionKey, sessionRevision, writesSuspended }; }, [accountId, selectionKey, sessionRevision, writesSuspended]);
  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  const pending = receipt.request;
  const review = reviewed?.key === selectionKey && reviewed.revision === sessionRevision ? reviewed.review : null;
  const visibleReview = terminal ? { entries: terminal.review } : review;
  const valid = !!review && selection.length > 0 && review.entries.length === selection.length && review.entries.every((entry) => selection.some((item) => item.draftId === entry.draftId) && !entry.isStale && !Object.keys(entry.errors).length);

  useEffect(() => { onPendingChange?.(!!pending); onDirtyChange?.(!!pending, !!pending); }, [pending, onPendingChange, onDirtyChange]);
  useEffect(() => { if (message) errorSummary.current?.focus(); }, [message]);
  useEffect(() => {
    if (pending || terminal || receipt.storageFailed || writesSuspended || !selection.length) return;
    let current = true;
    void reviewGemDrafts(JSON.parse(selectionKey)).then((next) => {
      if (current) { setReviewed({ key: selectionKey, revision: sessionRevision, review: next }); setConfirmed(false); setMessage(''); }
    }).catch((error: unknown) => {
      if (!current) return;
      if (error instanceof GemReferenceAdminApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      setMessage('We could not load the combined review. Restore your service-admin session if needed, then review again.');
    });
    return () => { current = false; };
  }, [accountId, selectionKey, sessionRevision, writesSuspended, pending, terminal, receipt.storageFailed, reviewAttempt, selection.length, onAuthLost]);

  // Every asynchronous completion belongs to its account, mounted view and session.
  function stillCurrent(scope: typeof currentScope.current) {
    return active.current && currentScope.current.accountId === scope.accountId && currentScope.current.sessionRevision === scope.sessionRevision && !currentScope.current.writesSuspended;
  }
  function finish(outcome: GemReferencePublishOutcome, request: GemReferencePublishRequest) {
    if (outcome.requestId !== request.requestId) throw new Error('Publication receipt does not match.');
    clearPendingPublication(accountId);
    setReceipt({ request: null, storageFailed: false });
    if (outcome.code === 'published') { onDirtyChange?.(false, false); onPendingChange?.(false); onPublished(outcome); }
    else { setReviewed(null); setConfirmed(false); setTerminal(outcome); setMessage('Publication was rejected. Your selected drafts remain available. Repair any problems and review again before publishing.'); }
  }
  async function resolvePublication(request: GemReferencePublishRequest, retry: boolean) {
    if (inFlight.current || writesSuspended) return;
    const scope = currentScope.current;
    inFlight.current = true; setBusy(true); setMessage('');
    try {
      const outcome = await getGemPublication(request.requestId);
      if (!stillCurrent(scope)) return;
      if (outcome) finish(outcome, request);
      else if (retry) { const next = await publishGemDrafts(request); if (stillCurrent(scope)) finish(next, request); }
      else setMessage('The publication outcome is not yet known. Check its outcome or retry the identical publication.');
    } catch (error) {
      if (!stillCurrent(scope)) return;
      if (error instanceof GemReferenceAdminApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      setMessage('The publication outcome is uncertain. The original request is preserved. Check its outcome or retry the identical publication.');
    } finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  useEffect(() => {
    let current = true;
    if (pending && !writesSuspended) void Promise.resolve().then(() => { if (current) return resolvePublication(pending, false); });
    return () => { current = false; };
    // Recovery starts on mount and on a restored session; explicit buttons own subsequent queries.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accountId, sessionRevision, writesSuspended]);

  async function publish() {
    if (!valid || !confirmed || busy || inFlight.current || pending || writesSuspended || terminal || receipt.storageFailed) return;
    const request = { requestId: crypto.randomUUID(), drafts: selection.map((item) => ({ ...item })) };
    try { writePendingPublication(accountId, request); }
    catch { setMessage('We could not preserve the publication request in this browser. Allow session storage and try publishing again. Nothing was sent.'); return; }
    setReceipt({ request, storageFailed: false });
    inFlight.current = true; setBusy(true); setMessage('');
    const scope = currentScope.current;
    try { const outcome = await publishGemDrafts(request); if (stillCurrent(scope)) finish(outcome, request); }
    catch (error) {
      if (!stillCurrent(scope)) return;
      if (error instanceof GemReferenceAdminApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      setMessage('The publication outcome is uncertain. The original request is preserved. Check its outcome or retry the identical publication.');
    } finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  return <section className="gem-review">
    <header className="gem-page-heading"><div><h1>Review drafts</h1><p>Review the combined changes before publishing this batch to the shared library.</p></div></header>
    {message || receipt.storageFailed ? <div className="form-message error gem-error-summary" role="alert" tabIndex={-1} ref={errorSummary}>{receipt.storageFailed ? 'We could not read the pending publication in this browser. Restore session storage before continuing.' : message}</div> : null}
    {receipt.storageFailed ? <button type="button" onClick={() => setReceipt(recover(accountId))}>Retry publication recovery</button> : null}
    {pending ? <section aria-label="Pending publication"><h2>Publication awaiting confirmation</h2><p>{pending.drafts.length} {pending.drafts.length === 1 ? 'draft is' : 'drafts are'} reserved in the original batch. Resolve this publication before changing or publishing another batch.</p><p>Request: {pending.requestId}</p><div className="gem-editor-actions"><button type="button" disabled={busy || writesSuspended} onClick={() => void resolvePublication(pending, false)}>Check publication outcome</button><button type="button" disabled={busy || writesSuspended} onClick={() => void resolvePublication(pending, true)}>Retry identical publication</button></div></section> : null}
    {!selection.length && !pending ? <p>No drafts selected. Select saved drafts in the library to review them.</p> : null}
    {!visibleReview && !pending && selection.length && !message && !writesSuspended && !receipt.storageFailed ? <p role="status">Loading combined review…</p> : null}
    {busy ? <p role="status">Confirming publication…</p> : null}
    {visibleReview ? visibleReview.entries.map((entry, index) => <section className="gem-review-entry" aria-label={`Draft ${index + 1} changes`} key={entry.draftId}>
      <div className="gem-review-bar"><h2>Draft {index + 1}</h2><button type="button" disabled={!!pending || busy || writesSuspended} onClick={() => onEdit(entry.draftId)}>Edit draft</button></div>
      <p className="gem-state">Entry: {entry.entryId}</p>
      {entry.isStale ? <p className="form-message error" role="alert">This draft is stale. Edit it to reconcile the current published version before reviewing again.</p> : null}
      {Object.keys(entry.errors).length ? <div className="form-message error" role="alert"><p>Resolve these problems before publishing:</p><ul>{Object.entries(entry.errors).flatMap(([field, errors]) => errors.map((error) => <li key={`${field}:${error}`}>{fieldNames[field] ?? field}: {error}</li>))}</ul></div> : null}
      {!entry.changes.length ? <p>No field changes.</p> : entry.changes.map((change) => <div className="gem-review-field" key={change.field}><h3>{fieldNames[change.field] ?? change.field}</h3><div className="gem-review-values"><section aria-label={`${fieldNames[change.field] ?? change.field} before`}><h4>Before</h4><ReviewValue value={change.before} /></section><section aria-label={`${fieldNames[change.field] ?? change.field} after`}><h4>After</h4><ReviewValue value={change.after} /></section></div></div>)}
    </section>) : null}
    {!pending && !receipt.storageFailed && selection.length ? <div className="gem-review-publish"><label className="gem-check"><input type="checkbox" checked={confirmed && !!review} disabled={!valid || busy || writesSuspended || !!terminal} onChange={(event) => setConfirmed(event.target.checked)} />I confirm these reviewed changes should be published.</label><div className="gem-editor-actions"><button type="button" disabled={!valid || !confirmed || busy || writesSuspended || !!terminal} onClick={() => void publish()}>Publish {selection.length} {selection.length === 1 ? 'draft' : 'drafts'}</button><button type="button" disabled={busy || writesSuspended} onClick={() => { setReviewed(null); setConfirmed(false); setTerminal(null); setReviewAttempt((value) => value + 1); }}>Review again</button></div></div> : null}
  </section>;
}
