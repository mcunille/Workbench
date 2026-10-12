import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { createTenantGem, getGem, updateTenantGem, saveGemOverrides, resetGem, GemReferenceApiError, type GemOrigin, type GemReferenceDetailResponse, type GemReferenceTenantProblemResponse } from '../../api/gemReference';
import { buildGemOverrides, draftFromGem, newGemDraft } from './gemEditorDraft';
import { GemTenantFields } from './GemTenantFields';
import { GemConflictComparison } from './GemConflictComparison';
import { GemConfirmationDialog } from './GemConfirmationDialog';
import { fieldLabels } from './gemPresentation';
import './gem-reference.css';
export type GemTenantEditorProps = { id?: string; origin?: GemOrigin; onDirtyChange: (dirty: boolean, uncertain: boolean) => void; onAuthLost: () => void; onSaved: (entry: GemReferenceDetailResponse) => void; onChanged: () => void; onCancel: () => void };
type Mode = 'loading' | 'editing' | 'saving' | 'uncertain' | 'checking' | 'comparison' | 'load-failed';
export function GemTenantEditor({ id, origin = 'tenant', onDirtyChange, onAuthLost, onSaved, onChanged, onCancel }: GemTenantEditorProps) {
  const [draft, setDraft] = useState(() => newGemDraft(id ?? crypto.randomUUID()));
  const [baseline, setBaseline] = useState<GemReferenceDetailResponse>();
  const [snapshot, setSnapshot] = useState(() => JSON.stringify(draft));
  const [current, setCurrent] = useState<GemReferenceDetailResponse>();
  const [mode, setMode] = useState<Mode>(id ? 'loading' : 'editing');
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [resetConfirmation, setResetConfirmation] = useState(false);
  const [loadAttempt, setLoadAttempt] = useState(0);
  const busy = useRef(false);
  const alive = useRef(true);
  const heading = useRef<HTMLHeadingElement>(null);
  const feedback = useRef<HTMLDivElement>(null);
  const changed = JSON.stringify(draft) !== snapshot;
  const locked = mode !== 'editing';
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => { onDirtyChange(changed || mode === 'saving' || mode === 'uncertain' || mode === 'checking' || mode === 'comparison', mode === 'saving' || mode === 'uncertain' || mode === 'checking'); }, [changed, mode, onDirtyChange]);
  useEffect(() => { if (mode === 'editing') heading.current?.focus(); }, [mode]);
  useEffect(() => { if (message) feedback.current?.focus(); }, [message]);
  useEffect(() => {
    if (!id) return;
    let active = true;
    const controller = new AbortController();
    void getGem(id, origin, controller.signal).then(entry => {
      if (!active) return;
      const next = draftFromGem(entry);
      setBaseline(entry); setDraft(next); setSnapshot(JSON.stringify(next)); setMode('editing');
    }).catch((error: unknown) => {
      if (!active) return;
      if ((error instanceof GemReferenceApiError || error instanceof ApiError) && (error.status === 401 || error.status === 403)) { onAuthLost(); return; }
      setMessage(error instanceof GemReferenceApiError && error.status === 404 ? 'Gem reference not found.' : 'We could not load this entry. Try again.'); setMode('load-failed');
    });
    return () => { active = false; controller.abort(); };
  }, [id, origin, loadAttempt, onAuthLost]);

  function accept(entry: GemReferenceDetailResponse, notify: boolean) {
    const next = draftFromGem(entry);
    setBaseline(entry); setDraft(next); setSnapshot(JSON.stringify(next)); setCurrent(undefined); setErrors({}); setMode('editing');
    setMessage(notify ? 'Changes saved.' : 'Current entry loaded.');
    onDirtyChange(false, false);
    if (notify) onSaved(entry); else onChanged();
  }
  function authFailure(error: unknown) {
    if ((error instanceof GemReferenceApiError || error instanceof ApiError) && (error.status === 401 || error.status === 403)) { onAuthLost(); return true; }
    return false;
  }
  async function write(reset = false) {
    if (busy.current || mode !== 'editing') return;
    busy.current = true; setMode('saving'); setErrors({}); setMessage(''); setResetConfirmation(false);
    try {
      let result: GemReferenceDetailResponse;
      if (baseline) {
        if (!baseline.effectiveVersion) { setMode('editing'); setMessage('Reload this entry before saving; its current versions are unavailable.'); return; }
        result = reset ? await resetGem(baseline.id, null, baseline.effectiveVersion)
          : baseline.origin === 'workbench' ? await saveGemOverrides(baseline.id, buildGemOverrides(draft), baseline.effectiveVersion)
          : await updateTenantGem(baseline.id, draft.content, baseline.effectiveVersion);
      } else result = await createTenantGem(draft.content);
      if (alive.current) accept(result, true);
    } catch (error) {
      if (!alive.current || authFailure(error)) return;
      if (error instanceof GemReferenceApiError && error.status < 500) {
        const problem = error.problem as Partial<GemReferenceTenantProblemResponse> | undefined;
        setErrors(problem?.errors ?? {});
        if (problem?.code === 'stale_entry' && problem.current?.id === draft.content.id && problem.current.origin === origin) {
          setCurrent(problem.current); setMode('comparison'); setMessage('This entry changed. Review the current entry before retrying.');
        } else {
          setMode('editing'); setMessage(error.status === 404 ? 'This entry is no longer available. Your draft is preserved.' : 'Changes were not saved. Review the field messages and try again.');
        }
      } else { setMode('uncertain'); setMessage('Save not confirmed. Your request may have completed. Check the current entry before trying again.'); }
    } finally { busy.current = false; }
  }
  async function check() {
    if (busy.current) return;
    busy.current = true; setMode('checking'); setMessage('Checking current saved entry…');
    try {
      const result = await getGem(draft.content.id, baseline?.origin === 'workbench' ? 'workbench' : 'tenant');
      if (!alive.current) return;
      setCurrent(result); setMode('comparison'); setMessage('Current saved entry found. Review it beside your draft.');
    } catch (error) {
      if (!alive.current || authFailure(error)) return;
      if (!baseline && !id && error instanceof GemReferenceApiError && error.status === 404) {
        setMode('editing'); setMessage('No saved entry was found. You can deliberately retry Save changes with the same entry ID.');
      } else { setMode('uncertain'); setMessage('We could not check the saved entry. Your draft is preserved; try checking again.'); }
    } finally { busy.current = false; }
  }
  function keepDraft() {
    if (!current) return;
    // Refresh inherited values while retaining deliberate tenant choices and their sources.
    if (current.origin === 'workbench') {
      const refreshed = draftFromGem(current).content;
      for (const [field, choice] of Object.entries(draft.overrides)) {
        if (choice.state !== 'inherit') Object.assign(refreshed, { [field]: draft.content[field as keyof typeof draft.content] });
      }
      setDraft({ content: { ...refreshed, sources: draft.content.sources }, overrides: draft.overrides });
    }
    setBaseline(current); setCurrent(undefined); setMode('editing'); setMessage('Draft kept with current versions. Review your choices, then Save changes.');
    // Adopting new concurrency tokens is itself an unsaved decision, even for identical values.
    setSnapshot('');
  }
  return <article className="reference-page reference-editor">
    <header className="page-heading"><div><h1 ref={heading} tabIndex={-1}>{id ? origin === 'workbench' ? 'Customize gem reference' : 'Edit tenant entry' : 'Add tenant entry'}</h1><p className="lede">{origin === 'workbench' ? 'Choose what your tenant inherits, replaces or clears. Review Workbench values beside your choices.' : 'Maintain a reference for your tenant. Sources help distinguish documented facts from personal notes.'}</p></div></header>
    {message ? <div ref={feedback} tabIndex={-1} className="form-message" role={mode === 'load-failed' || Object.keys(errors).length || mode === 'uncertain' ? 'alert' : 'status'}><p>{message}</p>{Object.entries(errors).map(([field, reasons]) => <p key={field}><strong>{fieldLabels[field] ?? field}: </strong>{reasons.join(' ')}</p>)}</div> : null}
    {mode === 'loading' ? <p role="status">Loading gem entry…</p> : mode === 'load-failed' ? <button type="button" onClick={() => { setMode('loading'); setMessage(''); setLoadAttempt(value => value + 1); }}>Retry load</button> : <>
      {baseline?.needsReview ? <section className="reference-review" aria-label="Needs review"><h2>Needs review</h2><p>Reconcile the conflicting fields before saving. Unrelated changes cannot be saved while the entry remains invalid.</p><ul>{Object.entries(baseline.reviewReasons ?? {}).flatMap(([field, reasons]) => reasons.map(reason => <li key={`${field}:${reason}`}><strong>{fieldLabels[field] ?? field}: </strong>{reason}</li>))}</ul></section> : null}
      {mode === 'comparison' && current ? <GemConflictComparison current={current} draft={draft} onUseCurrent={() => accept(current, false)} onKeepDraft={keepDraft} /> : null}
      {mode === 'uncertain' ? <button type="button" onClick={() => void check()}>Check current entry</button> : null}
      <form onSubmit={event => { event.preventDefault(); void write(); }}>
        <GemTenantFields draft={draft} entry={baseline} errors={errors} onChange={setDraft} disabled={locked} />
        <p className="reference-attribution">Unsaved changes remain in this page only. Reloading or ending your session discards them.</p>
        <div className="button-row"><button type="submit" disabled={locked}>{mode === 'saving' ? 'Saving…' : 'Save changes'}</button><button type="button" className="secondary" disabled={mode === 'saving' || mode === 'checking'} onClick={onCancel}>Cancel</button>{baseline?.origin === 'workbench' ? <button type="button" className="secondary" disabled={locked} onClick={() => setResetConfirmation(true)}>Reset all fields to Workbench</button> : null}</div>
      </form>
    </>}
    {resetConfirmation ? <GemConfirmationDialog title="Reset tenant customizations?" cancel={() => setResetConfirmation(false)}><p>This restores every current Workbench value and source. All tenant customizations and unsaved changes will be discarded.</p><div className="button-row"><button type="button" autoFocus className="secondary" onClick={() => setResetConfirmation(false)}>Keep editing</button><button type="button" onClick={() => void write(true)}>Reset all fields</button></div></GemConfirmationDialog> : null}
  </article>;
}
