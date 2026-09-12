import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import { ApiError } from '../../api/auth';
import { createDraft, updateDraft, getDraft, DraftError, type DraftContent, type DraftOrder, type CreateDraftRequest, type UpdateDraftRequest, type SaveReceipt } from '../../api/purchaseOrders';
import { DraftSupplier } from './DraftSupplier';
import { DraftComparison } from './DraftComparison';
import './purchasing.css';
import { ClearPricesDialog } from './ClearPricesDialog';
import { formatReferencePrice } from './referencePrice';
import { ReferencePriceField } from './ReferencePriceField';
import { deleteDraft, type DeleteDraftRequest } from '../../api/purchaseOrders';
import { DeleteDraftDialog } from './DeleteDraftDialog';

type Mode = 'loading' | 'editing' | 'saving' | 'uncertain' | 'current-loading' | 'current-failed' | 'conflict-loading' | 'conflict-failed' | 'comparison' | 'blocked' | 'load-failed' | 'deleting' | 'delete-uncertain';
type Submission = { id?: string; body: CreateDraftRequest | UpdateDraftRequest };
interface Props {
  id?: string;
  onDirtyChange(dirty: boolean, uncertain: boolean): void;
  onAuthLost(): void;
  onSaved(): void;
  onCreated(id: string): void;
  onCancel(): void;
}
const displayDraft = (draft: DraftContent): DraftContent => ({ ...draft, entries: draft.entries.map(entry => ({ ...entry, indicativePrice: formatReferencePrice(entry.indicativePrice) })) });
const emptyDraft = (): DraftContent => ({ title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: null, notes: null, sourceLinks: [], entries: [] });
const fieldId = (path: string) => `po-${path.replace(/[^a-zA-Z0-9]/g, '-')}`;
const optional = (text: string) => text === '' ? null : text;

function EntryDetails({ populated, invalid, children }: { populated: boolean; invalid: boolean; children: ReactNode }) {
  const [expanded, setExpanded] = useState(populated);
  const [wasPopulated, setWasPopulated] = useState(populated);
  if (populated !== wasPopulated) {
    setWasPopulated(populated);
    if (populated) setExpanded(true);
  }
  return <details className="po-entry-details" open={expanded || invalid} onToggle={event => setExpanded(event.currentTarget.open)}>
    <summary>Notes and source</summary>
    <div className="po-entry-secondary">{children}</div>
  </details>;
}

export function DraftEditor({ id: initialId, onDirtyChange, onAuthLost, onSaved, onCreated, onCancel }: Props) {
  const [supplierExpanded, setSupplierExpanded] = useState(!initialId);
  const [supplierDirty, setSupplierDirty] = useState(false);
  const [supplierUncertain, setSupplierUncertain] = useState(false);
  const reportSupplierDirty = useCallback((dirty: boolean, uncertain: boolean) => { setSupplierDirty(dirty); setSupplierUncertain(uncertain); }, []);
  const [clearingPrices, setClearingPrices] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const deletion = useRef<DeleteDraftRequest | undefined>(undefined);
  const [toolbarPinned, setToolbarPinned] = useState(false);
  const toolbarStart = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const marker = toolbarStart.current;
    if (!marker || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(([entry]) => {
      setToolbarPinned(!entry.isIntersecting && entry.boundingClientRect.top < 0);
    });
    observer.observe(marker);
    return () => observer.disconnect();
  }, []);
  const [id, setId] = useState(initialId);
  const [draft, setDraft] = useState(emptyDraft);
  const addedEntry = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!addedEntry.current) return;
    const index = draft.entries.findIndex(entry => entry.id === addedEntry.current);
    if (index < 0) return;
    const description = document.getElementById(fieldId(`draft.entries[${index}].description`));
    description?.focus({ preventScroll: true });
    description?.scrollIntoView?.({ block: 'center', behavior: 'instant' });
    addedEntry.current = undefined;
  }, [draft.entries]);
  const [baseline, setBaseline] = useState<DraftOrder>();
  const [current, setCurrent] = useState<DraftOrder>();
  const [mode, setMode] = useState<Mode>(initialId ? 'loading' : 'editing');
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [savedAt, setSavedAt] = useState<string>();
  const submitted = useRef<Submission | undefined>(undefined);
  const confirmed = useRef<SaveReceipt | undefined>(undefined);
  const busy = useRef(false);
  const alive = useRef(true);
  const firstId = useRef(initialId);
  const supplierAccessLost = useCallback(() => {
    setDraft(emptyDraft()); setBaseline(undefined); setCurrent(undefined); setSavedAt(undefined);
    submitted.current = undefined; confirmed.current = undefined; deletion.current = undefined;
    setSupplierDirty(false); setSupplierUncertain(false); setMode('blocked'); onDirtyChange(false, false); onAuthLost();
  }, [onAuthLost, onDirtyChange]);

  useEffect(() => {
    alive.current = true;
    let currentRead = true;
    if (firstId.current) {
      void getDraft(firstId.current).then(value => {
        if (!alive.current || !currentRead) return;
        setBaseline({ ...value, draft: displayDraft(value.draft) }); setDraft(displayDraft(value.draft)); setSavedAt(value.updatedAtUtc); setMode('editing');
      }, error => {
        if (!alive.current || !currentRead) return;
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
        setMessage(error instanceof ApiError && error.status === 404 ? 'This draft is unavailable.' : 'The draft could not be loaded.');
        setMode(error instanceof ApiError && error.status === 404 ? 'blocked' : 'load-failed');
      });
    }
    return () => { alive.current = false; currentRead = false; };
  }, [onAuthLost]);
  const changed = JSON.stringify(draft) !== JSON.stringify(baseline?.draft ?? emptyDraft());
  const uncertain = supplierUncertain || mode === 'uncertain' || mode === 'saving' || mode === 'deleting' || mode === 'delete-uncertain';
  const dirty = supplierDirty || uncertain || mode === 'comparison' || mode.startsWith('conflict-') || ((mode === 'editing' || mode === 'blocked') && changed);
  useEffect(() => { onDirtyChange(dirty, uncertain); }, [dirty, uncertain, onDirtyChange]);
  useEffect(() => () => onDirtyChange(false, false), [onDirtyChange]);
  useEffect(() => {
    const first = Object.keys(errors)[0];
    if (first) document.getElementById(fieldId(first))?.focus();
  }, [errors]);
  const frozen = mode !== 'editing';
  const hasPrices = draft.entries.some(entry => entry.indicativePrice !== null);
  const currencyTransition = !!baseline?.draft.currency && (draft.currency?.trim().toUpperCase() ?? null) !== baseline.draft.currency;

  function accessFailure(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
      setDraft(emptyDraft()); submitted.current = undefined; confirmed.current = undefined; deletion.current = undefined; setCurrent(undefined); setBaseline(undefined);
      setMode('blocked'); onDirtyChange(false, false); onAuthLost(); return true;
    }
    if (error instanceof ApiError && error.status === 404) { setMode('blocked'); setMessage('This draft is unavailable.'); return true; }
    return false;
  }
  async function loadCurrent(reason: 'confirmed' | 'conflict' | 'initial', target: string, receipt?: SaveReceipt) {
    setMode(reason === 'confirmed' ? 'current-loading' : reason === 'conflict' ? 'conflict-loading' : 'loading');
    setMessage('');
    try {
      const latest = await getDraft(target);
      if (!alive.current) return;
      if (reason === 'conflict' || (receipt && latest.version !== receipt.savedVersion)) {
        setCurrent(latest); setMode('comparison');
      } else {
        setBaseline({ ...latest, draft: displayDraft(latest.draft) }); setDraft(displayDraft(latest.draft)); setCurrent(undefined); setMode('editing');
        setSavedAt(latest.updatedAtUtc); submitted.current = undefined; confirmed.current = undefined;
        onDirtyChange(false, false);
      }
    } catch (error) {
      if (!alive.current || accessFailure(error)) return;
      setMode(reason === 'confirmed' ? 'current-failed' : reason === 'conflict' ? 'conflict-failed' : 'load-failed');
      setMessage(reason === 'confirmed' ? 'Saved; current version could not be loaded.' : reason === 'conflict' ? 'Your changes are kept; the current draft could not be loaded for comparison.' : 'The draft could not be loaded.');
    }
  }
  async function retryRead() {
    if (busy.current || !id) return;
    busy.current = true;
    try { await loadCurrent(confirmed.current ? 'confirmed' : mode === 'load-failed' ? 'initial' : 'conflict', id, confirmed.current); }
    finally { busy.current = false; }
  }
  async function save() {
    if (busy.current || (mode !== 'editing' && mode !== 'uncertain') || (mode === 'editing' && baseline && !changed)) return;
    busy.current = true;
    const request = submitted.current ?? { id, body: baseline
      ? { requestId: crypto.randomUUID(), expectedVersion: baseline.version, draft: structuredClone(draft) }
      : { requestId: crypto.randomUUID(), draft: structuredClone(draft) } };
    submitted.current = request;
    setErrors({}); setMessage(''); setMode('saving'); onDirtyChange(true, true);
    try {
      const receipt = request.id ? await updateDraft(request.id, request.body as UpdateDraftRequest) : await createDraft(request.body);
      if (!alive.current) return;
      confirmed.current = receipt; submitted.current = undefined;
      setId(receipt.draftOrderId); setSavedAt(receipt.completedAtUtc); onSaved(); onDirtyChange(false, false);
      if (!request.id) onCreated(receipt.draftOrderId);
      await loadCurrent('confirmed', receipt.draftOrderId, receipt);
    } catch (error) {
      if (!alive.current || accessFailure(error)) return;
      if (error instanceof DraftError && error.code === 'draft_version_conflict' && request.id) {
        submitted.current = undefined; await loadCurrent('conflict', request.id);
      } else if (error instanceof DraftError && error.code === 'draft_request_conflict') {
        setMode('blocked'); setMessage('This save request conflicts with a previous request. Your input is kept. Return to purchase orders and reopen the saved draft to review it.');
      } else if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
        submitted.current = undefined; setMode('editing');
        setErrors(error instanceof DraftError ? error.errors : {});
        setMessage(error instanceof DraftError && error.code === 'supplier_selection_conflict' ? 'The selected supplier is no longer available for new orders. Choose an active supplier or keep these details as one-off, then save again.' : error instanceof DraftError && error.code === 'draft_contract_reload_required' ? 'This draft contract has changed. Your input is kept. Reload before submitting again.' : error.status === 413 ? 'This draft is too large. Shorten it and save again.' : 'Review the draft fields and save again.');
      } else {
        setMode('uncertain'); setMessage('We couldn’t confirm your save.');
      }
    } finally { busy.current = false; }
  }
  function chooseCurrent(keepLocal: boolean) {
    if (!current) return;
    setBaseline({ ...current, draft: displayDraft(current.draft) }); if (!keepLocal) setDraft(displayDraft(current.draft));
    setSavedAt(current.updatedAtUtc); setCurrent(undefined); submitted.current = undefined; confirmed.current = undefined; setMode('editing');
  }
  async function removeDraft() {
    if (busy.current || !id || !baseline || (mode !== 'editing' && mode !== 'delete-uncertain')) return;
    busy.current = true;
    const request = deletion.current ?? { requestId: crypto.randomUUID(), expectedVersion: baseline.version };
    deletion.current = request;
    setConfirmingDelete(false); setMode('deleting'); setErrors({}); setMessage(''); onDirtyChange(true, true);
    try {
      await deleteDraft(id, request);
      if (!alive.current) return;
      deletion.current = undefined;
      setDraft(emptyDraft()); setBaseline(undefined); setCurrent(undefined); setMode('blocked');
      onDirtyChange(false, false); onSaved(); onCancel();
    } catch (error) {
      if (!alive.current || accessFailure(error)) return;
      if (error instanceof DraftError && error.code === 'draft_version_conflict') {
        deletion.current = undefined;
        await loadCurrent('conflict', id);
      } else if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
        deletion.current = undefined;
        setMode(error instanceof DraftError && error.code === 'draft_request_conflict' ? 'blocked' : 'editing');
        setMessage('The draft could not be deleted. Return to purchase orders and reopen it before trying again.');
      } else {
        setMode('delete-uncertain'); setMessage('We couldn’t confirm the deletion. Check and retry to find out whether it completed.');
      }
    } finally { busy.current = false; }
  }
  function field(path: string, label: string, value: string | null, change: (value: string | null) => void, options: { multiline?: boolean; placeholder?: string; disabled?: boolean } = {}) {
    const controlId = fieldId(path);
    const error = errors[path]?.join(' ');
    const common = { id: controlId, name: path, value: value ?? '', onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => change(optional(event.target.value)), disabled: frozen || options.disabled, placeholder: options.placeholder ?? ' ', 'aria-invalid': !!error, 'aria-describedby': error ? `${controlId}-error` : undefined };
    return <div className="po-field" key={path}><FloatingField htmlFor={controlId} label={label}>{options.multiline ? <textarea {...common} rows={2} /> : <input {...common} />}</FloatingField>{error ? <p id={`${controlId}-error`} className="form-message error">{error}</p> : null}</div>;
  }
  const saveDisabled = (mode !== 'editing' && mode !== 'uncertain') || (mode === 'editing' && !!baseline && !changed);
  const saveLabel = mode === 'saving' ? 'Saving…' : mode === 'uncertain' ? 'Check and retry' : 'Save draft';
  const saveStatus = mode === 'saving' ? 'Saving draft…'
    : mode.endsWith('loading') ? 'Loading current draft…'
    : savedAt ? `Saved ${new Date(savedAt).toLocaleString()}${dirty ? ' · Unsaved changes' : ''}`
    : changed ? 'Unsaved changes' : '';

  return (
    <section className="editor po-editor">
      {confirmingDelete && baseline && !frozen ? <DeleteDraftDialog title={baseline.draft.title ?? 'Untitled draft'}
        cancel={() => setConfirmingDelete(false)} confirm={() => void removeDraft()} /> : null}
      {clearingPrices && !frozen ? <ClearPricesDialog count={draft.entries.filter(entry => entry.indicativePrice !== null).length} cancel={() => setClearingPrices(false)} clear={() => {
        setDraft({ ...draft, entries: draft.entries.map(entry => ({ ...entry, indicativePrice: null })) });
        setClearingPrices(false);
      }} /> : null}
      <div ref={toolbarStart} className="po-toolbar-start" aria-hidden="true" />
      <div className={`po-editor-toolbar${toolbarPinned ? ' is-pinned' : ''}`}>
        <button type="button" className="quiet po-back" aria-label="Back to purchase orders" onClick={onCancel}>
          <Icon name="back" />Purchase orders
        </button>
        <button type="submit" form="po-draft-form" className="primary" disabled={saveDisabled}>
          {saveLabel}
        </button>
      </div>
      <header className="po-editor-header">
        <div className="po-heading">
          <h1>{baseline?.poReference ?? current?.poReference ?? (id ? 'Purchase order' : 'New purchase order')}</h1><span className="po-badge">Draft</span>
        </div>
        {saveStatus ? <p className="po-save-status" role="status">{saveStatus}</p> : null}
      </header>
      {message && !Object.keys(errors).length ? <p role="alert" className="form-message error">{message}</p> : null}
      {mode === 'delete-uncertain' ? <button type="button" className="secondary" onClick={() => void removeDraft()}>Check and retry deletion</button> : null}
      {mode === 'deleting' ? <p role="status">Deleting draft…</p> : null}
      {mode === 'current-failed' || mode === 'conflict-failed' || mode === 'load-failed' ? (
        <button className="secondary" onClick={() => void retryRead()}>Load current draft</button>
      ) : null}
      {mode === 'comparison' && current ? (
        <section className="po-comparison-panel" aria-label="Compare draft versions">
          <div className="po-section-heading">
            <div><h2>Review newer changes</h2><p>Compare the current saved draft with your changes before continuing.</p></div>
          </div>
          <div className="po-comparison">
            <DraftComparison heading="Current saved" draft={current.draft} />
            <DraftComparison heading="Your changes" draft={draft} />
          </div>
          <div className="button-row">
            <button type="button" className="secondary" onClick={() => chooseCurrent(false)}>Use saved version</button>
            <button type="button" className="primary" onClick={() => chooseCurrent(true)}>Continue with my changes</button>
          </div>
        </section>
      ) : null}
      <form id="po-draft-form" className="form-stack" noValidate onSubmit={event => { event.preventDefault(); void save(); }}>
        {Object.keys(errors).length ? (
          <div role="alert" className="po-validation-summary" tabIndex={-1} id={fieldId('draft')}>
            <h2>Review these fields</h2>
            <ul>{Object.entries(errors).flatMap(([path, messages]) => messages.map((text, index) => (
              <li key={`${path}-${index}`}>
                <a href={`#${fieldId(path)}`} onClick={event => {
                  event.preventDefault();
                  const target = document.getElementById(fieldId(path));
                  let disclosure = target?.closest('details');
                  while (disclosure) { disclosure.setAttribute('open', ''); disclosure = disclosure.parentElement?.closest('details'); }
                  target?.focus();
                }}>{text}</a>
              </li>
            )))}</ul>
          </div>
        ) : null}
        <section className="po-form-section" aria-label="Order details">
          <div className="po-header-fields po-title-fields">
            {field('draft.title', 'Title', draft.title, title => setDraft({ ...draft, title }))}

          </div>
        </section>
        <details className="po-form-section po-supplier-section" open={supplierExpanded || Object.keys(errors).some(key => key.startsWith('draft.supplier') || key === 'draft.platform')} onToggle={event => setSupplierExpanded(event.currentTarget.open)}>
          <summary className="po-supplier-summary"><span>Supplier details</span><span className="po-supplier-summary-context">{[draft.supplierName, draft.platform].filter(Boolean).join(' · ') || 'Add a supplier or one-off contact'}</span></summary>
          <DraftSupplier draft={draft} archived={!!baseline?.supplierIsArchived && baseline.draft.supplierId === draft.supplierId} frozen={frozen} onChange={setDraft} onAuthLost={supplierAccessLost} onDirtyChange={reportSupplierDirty} />
          <div className="po-header-fields">
            {field('draft.supplierName', 'Supplier name', draft.supplierName, supplierName => setDraft({ ...draft, supplierName }))}
            {field('draft.platform', 'Platform', draft.platform, platform => setDraft({ ...draft, platform }), { placeholder: 'e.g. Instagram, Retail' })}
            {field('draft.supplierOrderReference', 'Supplier order reference', draft.supplierOrderReference, supplierOrderReference => setDraft({ ...draft, supplierOrderReference }))}
          </div>
          <details className="po-contact-details" open={Object.keys(errors).some(key => /^draft\.supplier(ContactName|Email|Phone|Website|PostalAddress)$/.test(key)) || undefined}>
            <summary>Contact details (optional)</summary>
            <div className="po-header-fields">
              {field('draft.supplierContactName', 'Supplier contact name', draft.supplierContactName, supplierContactName => setDraft({ ...draft, supplierContactName }))}
              {field('draft.supplierEmail', 'Supplier email', draft.supplierEmail, supplierEmail => setDraft({ ...draft, supplierEmail }))}
              {field('draft.supplierPhone', 'Supplier phone', draft.supplierPhone, supplierPhone => setDraft({ ...draft, supplierPhone }))}
              {field('draft.supplierWebsite', 'Supplier website', draft.supplierWebsite, supplierWebsite => setDraft({ ...draft, supplierWebsite }))}
              {field('draft.supplierPostalAddress', 'Supplier postal address', draft.supplierPostalAddress, supplierPostalAddress => setDraft({ ...draft, supplierPostalAddress }), { multiline: true })}
            </div>
          </details>
        </details>
        <section className="po-form-section" aria-labelledby="po-entries-heading">
          <div className="po-section-heading">
            <div><h2 id="po-entries-heading">Shopping list</h2><p>Prices are reference amounts; no total is calculated.</p></div>
          </div>
          <div className="po-currency-row">
            {field('draft.currency', 'Currency', draft.currency, currency => setDraft({ ...draft, currency }), {
              placeholder: 'Not set', disabled: hasPrices && !!baseline?.draft.currency,
            })}
            <div className="po-field-help">
              <p>Choose a three-letter currency when entering a price. Clear reference prices before changing currency.</p>
              {hasPrices ? (
                <button className="quiet" type="button" disabled={frozen} onClick={() => setClearingPrices(true)}>Clear all reference prices</button>
              ) : null}
            </div>
          </div>
          {currencyTransition ? <p role="status">Save the changed currency with all prices cleared before entering new prices.</p> : null}
          {draft.entries.length === 0 ? <p className="po-section-empty">Add an entry to start your shopping list. You can save an empty draft too.</p> : null}
          {draft.entries.map((entry, index) => {
            const updateEntry = (key: keyof typeof entry, value: string | null) => setDraft({
              ...draft, entries: draft.entries.map(old => old.id === entry.id ? { ...old, [key]: value } : old),
            });
            return (
              <fieldset className="po-entry" key={entry.id} aria-labelledby={`po-entry-title-${entry.id}`}>
                <div className="po-entry-heading">
                  <h3 id={`po-entry-title-${entry.id}`}>Entry {index + 1}</h3>
                  <button className="quiet danger" type="button" disabled={frozen} onClick={() => setDraft({
                    ...draft, entries: draft.entries.filter(old => old.id !== entry.id),
                  })}>Remove entry {index + 1}</button>
                </div>
                <div className="po-header-fields">
                  {field(`draft.entries[${index}].description`, `Description ${index + 1}`, entry.description, value => updateEntry('description', value))}
                  <div className="po-entry-price"><ReferencePriceField id={fieldId(`draft.entries[${index}].indicativePrice`)} index={index + 1}
                    value={entry.indicativePrice} onChange={value => updateEntry('indicativePrice', value)}
                    disabled={frozen || currencyTransition} error={errors[`draft.entries[${index}].indicativePrice`]?.join(' ')} />
                  {entry.indicativePrice === null ? <p className="po-price-state">Price: Unknown</p> : null}</div>
                </div>
                <EntryDetails populated={!!(entry.notes || entry.sourceLink)} invalid={!!(errors[`draft.entries[${index}].notes`] || errors[`draft.entries[${index}].sourceLink`])}>
                  {field(`draft.entries[${index}].notes`, `Entry notes ${index + 1}`, entry.notes, value => updateEntry('notes', value), { multiline: true })}
                  {field(`draft.entries[${index}].sourceLink`, `Entry source link ${index + 1}`, entry.sourceLink, value => updateEntry('sourceLink', value))}
                </EntryDetails>
              </fieldset>
            );
          })}
          <div className="po-add-entry">
            <button className="secondary" type="button" disabled={frozen} onClick={() => {
              const entryId = crypto.randomUUID();
              addedEntry.current = entryId;
              setDraft({ ...draft, entries: [...draft.entries, { id: entryId, description: null, notes: null, sourceLink: null, indicativePrice: null }] });
            }}><Icon name="plus" />Add entry</button>
          </div>
        </section>
        <section className="po-form-section" aria-labelledby="po-context-heading">
          <div className="po-section-heading">
            <div><h2 id="po-context-heading">Notes and sources</h2><p>Keep the details you will want when you return to this draft.</p></div>
          </div>
          <div className="po-order-context">
            {field('draft.notes', 'Notes', draft.notes, notes => setDraft({ ...draft, notes }), { multiline: true })}
            <section aria-labelledby="po-links-heading">
              <div className="po-section-heading">
                <h3 id="po-links-heading">Source links</h3>
                <button className="secondary" type="button" disabled={frozen} onClick={() => setDraft({
                  ...draft, sourceLinks: [...draft.sourceLinks, ''],
                })}><Icon name="plus" />Add source link</button>
              </div>
              {draft.sourceLinks.length === 0 ? <p className="po-section-empty">Supplier pages and other references can go here.</p> : null}
              {draft.sourceLinks.map((link, index) => (
                <div className="po-removable" key={index}>
                  {field(`draft.sourceLinks[${index}]`, `Source link ${index + 1}`, link, value => setDraft({
                    ...draft, sourceLinks: draft.sourceLinks.map((old, position) => position === index ? value ?? '' : old),
                  }))}
                  <button className="quiet" type="button" disabled={frozen} onClick={() => setDraft({
                    ...draft, sourceLinks: draft.sourceLinks.filter((_, position) => position !== index),
                  })}>Remove source link {index + 1}</button>
                </div>
              ))}
            </section>
          </div>
        </section>
      </form>
      {baseline ? <div className="button-row"><button type="button" className="quiet danger" disabled={frozen}
        onClick={() => setConfirmingDelete(true)}>Delete draft</button></div> : null}
    </section>
  );
}
