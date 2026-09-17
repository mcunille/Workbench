import { RecoveryText } from '../../RecoveryText';
import { recoveryText } from '../../formatRecoveryText';
import { useCallback, useEffect, useRef, useState } from 'react';
import { FloatingField } from '../../FloatingField';
import { Icon } from '../../Icon';
import { ApiError } from '../../api/auth';
import { createDraft, updateDraft, getDraft, DraftError, type DraftContent, type DraftOrder, type CreateDraftRequest, type UpdateDraftRequest, type SaveReceipt } from '../../api/purchaseOrders';
import { DraftSupplier } from './DraftSupplier';
import { SupplierDialog } from './SupplierDialog';
import { DraftComparison } from './DraftComparison';
import './purchasing.css';
import { ClearPricesDialog } from './ClearPricesDialog';
import { formatReferencePrice } from './referencePrice';
import { DraftLineFields } from './DraftLineFields';
import { DraftLine } from './DraftLineDisclosure';
import { emptyLine, formatQuantity, hasLinePrice } from './draftLine';
import { useDraftCalculation } from './useDraftCalculation';
import { deleteDraft, type DeleteDraftRequest } from '../../api/purchaseOrders';
import { DeleteDraftDialog } from './DeleteDraftDialog';
import { DraftCharges } from './DraftCharges';
import { DiscountFields } from './DiscountFields';
import { DraftFinancialSummary } from './DraftFinancialSummary';
import { clearDraftAmounts, hasAdjustments, hasMonetaryAmounts, type Discount } from './draftFinances';

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
const displayDiscount = (discount: Discount | null | undefined): Discount | null => discount ? { ...discount, value: (discount.mode === 'fixed' ? formatReferencePrice(discount.value) : formatQuantity(discount.value)) ?? '' } : null;
const displayDraft = (draft: DraftContent): DraftContent => ({ ...draft, orderDiscount: displayDiscount(draft.orderDiscount), charges: (draft.charges ?? []).map(charge => ({ ...charge, amount: formatReferencePrice(charge.amount) })), entries: draft.entries.map(entry => ({ ...entry, discount: displayDiscount(entry.discount), quantity: formatQuantity(entry.quantity), indicativePrice: formatReferencePrice(entry.indicativePrice), price: formatReferencePrice(entry.price) })) });
const emptyDraft = (): DraftContent => ({ title: null, supplierName: null, supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: null, notes: null, sourceLinks: [], entries: [], orderDiscount: null, charges: [] });
const fieldId = (path: string) => `po-${path.replace(/[^a-zA-Z0-9]/g, '-')}`;
const optional = (text: string) => text === '' ? null : text;

export function DraftEditor({ id: initialId, onDirtyChange, onAuthLost, onSaved, onCreated, onCancel }: Props) {
  const [clearingSupplier, setClearingSupplier] = useState(false);
  const supplierSummary = useRef<HTMLElement>(null);
  const focusSupplierSummary = useRef(false);

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
  const [removedEntries, setRemovedEntries] = useState<{ entry: DraftContent['entries'][number]; index: number }[]>([]);
  const addEntryButton = useRef<HTMLButtonElement>(null);
  const addedEntry = useRef<string | undefined>(undefined);
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
    if (addedEntry.current || focusSupplierSummary.current) return;
    const first = Object.keys(errors)[0];
    if (first) document.getElementById(fieldId(first))?.focus();
  }, [errors]);
  useEffect(() => {
    if (!addedEntry.current) return;
    const index = draft.entries.findIndex(entry => entry.id === addedEntry.current);
    if (index < 0) { addEntryButton.current?.focus(); addedEntry.current = undefined; return; }
    const description = document.getElementById(fieldId(`draft.entries[${index}].description`));
    description?.closest('details.po-line-disclosure')?.setAttribute('open', '');
    description?.focus({ preventScroll: true });
    description?.scrollIntoView?.({ block: 'center', behavior: 'instant' });
    addedEntry.current = undefined;
  }, [draft.entries]);
  useEffect(() => { if (!clearingSupplier && focusSupplierSummary.current) { supplierSummary.current?.focus(); focusSupplierSummary.current = false; } }, [clearingSupplier]);
  const frozen = mode !== 'editing';
  const calculation = useDraftCalculation(draft, !frozen, supplierAccessLost);
  const visibleErrors = { ...calculation.errors, ...errors };
  const [undoBoundary, setUndoBoundary] = useState({ mode, currency: draft.currency });
  if (undoBoundary.mode !== mode || undoBoundary.currency !== draft.currency) {
    setUndoBoundary({ mode, currency: draft.currency });
    if (mode !== 'editing' || undoBoundary.currency !== draft.currency) setRemovedEntries([]);
  }
  const hasPrices = hasMonetaryAmounts(draft) || hasAdjustments(draft);
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
        setMessage(error instanceof DraftError && error.code === 'supplier_selection_conflict' ? 'The selected supplier is no longer available for new orders. Choose an active supplier or clear the supplier, then save again.' : error instanceof DraftError && error.code === 'draft_contract_reload_required' ? 'This draft contract has changed. Your input is kept. Reload before submitting again.' : error.status === 413 ? 'This draft is too large. Shorten it and save again.' : 'Review the draft fields and save again.');
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
    const error = visibleErrors[path]?.join(' ');
    const common = { id: controlId, name: path, value: value ?? '', onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => change(optional(event.target.value)), disabled: frozen || options.disabled, placeholder: options.placeholder ?? ' ', 'aria-invalid': !!error, 'aria-describedby': error ? `${controlId}-error` : undefined };
    return <div className="po-field" key={path}><FloatingField htmlFor={controlId} label={label}>{options.multiline ? <textarea {...common} rows={2} /> : <input {...common} />}</FloatingField>{error ? <p id={`${controlId}-error`} className="form-message error">{error}</p> : null}</div>;
  }
  const saveDisabled = (mode !== 'editing' && mode !== 'uncertain') || (mode === 'editing' && !!baseline && !changed);
  const saveLabel = mode === 'saving' ? 'Saving…' : mode === 'uncertain' ? 'Check and retry' : 'Save draft';
  const saveStatus = mode === 'saving' ? 'Saving…'
    : mode === 'uncertain' ? 'Save unconfirmed'
    : mode === 'deleting' ? 'Deleting…'
    : mode === 'delete-uncertain' ? 'Deletion unconfirmed'
    : mode === 'blocked' ? 'Editing unavailable'
    : mode === 'current-failed' ? 'Saved · refresh needed'
    : mode.endsWith('loading') ? 'Loading…'
    : dirty ? 'Unsaved changes' : savedAt ? 'Saved' : 'Not saved';
  function addLine() {
    const entryId = crypto.randomUUID();
    addedEntry.current = entryId;
    setDraft({ ...draft, entries: [...draft.entries, emptyLine(entryId)] });
  }

  return (
    <section className="editor po-editor">
      {clearingSupplier && !frozen ? <SupplierDialog title="Clear supplier?" cancel={() => setClearingSupplier(false)}>
        <p>Clear this PO’s supplier details and supplier order reference? The supplier stays in your directory. Save the draft to keep this change.</p>
        <div className="button-row po-dialog-footer"><button type="button" className="secondary" autoFocus onClick={() => setClearingSupplier(false)}>Cancel</button><button type="button" className="secondary danger" onClick={() => {
          setDraft({ ...draft, supplierId: null, supplierName: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null });
          setErrors(Object.fromEntries(Object.entries(errors).filter(([path]) => !path.startsWith('draft.supplier'))));
          focusSupplierSummary.current = true; setClearingSupplier(false);
        }}>Clear supplier</button></div>
      </SupplierDialog> : null}
      {confirmingDelete && baseline && !frozen ? <DeleteDraftDialog title={baseline.draft.title ?? 'Untitled draft'}
        cancel={() => setConfirmingDelete(false)} confirm={() => void removeDraft()} /> : null}
      {clearingPrices && !frozen ? <ClearPricesDialog count={draft.entries.filter(hasLinePrice).length} confirmedCharges={draft.charges.some(charge => baseline?.draft.charges.some(saved => saved.id === charge.id && saved.amountStatus === 'confirmed'))} cancel={() => setClearingPrices(false)} clear={reason => {
        setDraft(clearDraftAmounts(draft, baseline?.draft, reason));
        setClearingPrices(false);
      }} /> : null}
      <div ref={toolbarStart} className="po-toolbar-start" aria-hidden="true" />
      <div className={`po-editor-toolbar${toolbarPinned ? ' is-pinned' : ''}`}>
        <button type="button" className="quiet po-back" aria-label="Back to purchase orders" onClick={onCancel}>
          <Icon name="back" />Purchase orders
        </button>
        <div className="po-save-action"><p className="po-save-status" role="status">{saveStatus}</p>
          <button type="submit" form="po-draft-form" className="primary" disabled={saveDisabled}>{saveLabel}</button>
        </div>
      </div>
      <header className="po-editor-header">
        <div className="po-heading">
          <h1>{baseline?.poReference ?? current?.poReference ?? (id ? 'Purchase order' : 'New purchase order')}</h1><span className="po-badge">Draft</span>
        </div>
        {savedAt ? <p className="po-saved-time">Last saved {new Date(savedAt).toLocaleString()}</p> : null}
        {draft.entries.length || draft.charges.length || draft.orderDiscount ? <a className="po-estimate-jump" href="#po-purchase-estimate" onClick={() => document.getElementById('po-purchase-estimate')?.focus({ preventScroll: true })}>View purchase estimate</a> : null}
      </header>
      {message && !Object.keys(visibleErrors).length ? <p role="alert" className="form-message error">{message}</p> : null}
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
      {['uncertain', 'delete-uncertain', 'current-failed', 'conflict-failed', 'comparison', 'blocked'].includes(mode) && recoveryText(draft) ? <RecoveryText label="Purchase draft" text={recoveryText(draft)} /> : null}
      <form id="po-draft-form" className="form-stack" noValidate onSubmit={event => { event.preventDefault(); void save(); }}>
        {Object.keys(visibleErrors).length ? (
          <div role="alert" className="po-validation-summary" tabIndex={-1} id={fieldId('draft')}>
            <h2>Review these fields</h2>
            <ul>{Object.entries(visibleErrors).flatMap(([path, messages]) => messages.map((text, index) => (
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
        <details className="po-form-section po-supplier-section" open={supplierExpanded || Object.keys(visibleErrors).some(key => key.startsWith('draft.supplier') || key === 'draft.platform')} onToggle={event => setSupplierExpanded(event.currentTarget.open)}>
          <summary ref={supplierSummary} className="po-supplier-summary"><span className="po-supplier-summary-row"><span className="po-supplier-summary-copy"><span>Supplier details</span><span className="po-supplier-summary-context">{[draft.supplierName, draft.platform].filter(Boolean).join(' · ') || 'Add a supplier or one-off contact'}</span></span>{[draft.supplierId, draft.supplierName, draft.supplierContactName, draft.supplierEmail, draft.supplierPhone, draft.supplierWebsite, draft.supplierPostalAddress, draft.supplierOrderReference].some(Boolean) ? <button type="button" className="quiet danger" disabled={frozen} onClick={event => { event.preventDefault(); event.stopPropagation(); setClearingSupplier(true); }}>Clear supplier</button> : null}</span></summary>
          <DraftSupplier draft={draft} archived={!!baseline?.supplierIsArchived && baseline.draft.supplierId === draft.supplierId} frozen={frozen || clearingSupplier} onChange={setDraft} onAuthLost={supplierAccessLost} onDirtyChange={reportSupplierDirty} />
          <div className="po-header-fields">
            {field('draft.supplierName', 'Supplier name', draft.supplierName, supplierName => setDraft({ ...draft, supplierName }))}
            {field('draft.platform', 'Platform', draft.platform, platform => setDraft({ ...draft, platform }), { placeholder: 'e.g. Instagram, Retail' })}
            {field('draft.supplierOrderReference', 'Supplier order reference', draft.supplierOrderReference, supplierOrderReference => setDraft({ ...draft, supplierOrderReference }))}
          </div>
          <details className="po-contact-details" open={Object.keys(visibleErrors).some(key => /^draft\.supplier(ContactName|Email|Phone|Website|PostalAddress)$/.test(key)) || undefined}>
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
            <div><h2 id="po-entries-heading">Order lines</h2><p>Review each line or expand it to edit.</p></div>
            <button className="secondary" type="button" disabled={frozen} onClick={addLine}><Icon name="plus" />Add line</button>
          </div>
          <div className="po-currency-row">
            {field('draft.currency', 'Currency', draft.currency, currency => setDraft({ ...draft, currency }), {
              placeholder: 'Not set', disabled: !!baseline?.draft.currency && hasMonetaryAmounts(baseline.draft),
            })}
            <div className="po-field-help">
              <p>Prices, discounts and charges use this currency. To change it, clear all amounts and save first.</p>
              {hasPrices ? (
                <button className="quiet" type="button" disabled={frozen} onClick={() => setClearingPrices(true)}>Clear all amounts</button>
              ) : null}
            </div>
          </div>
          {currencyTransition ? <p role="status">Save the new currency before entering amounts.</p> : null}
          {draft.entries.length === 0 ? <p className="po-section-empty">Add a line to itemize your purchase. You can save an empty draft too.</p> : null}
          {draft.entries.map((entry, index) => {
            return (
              <DraftLine key={entry.id} entry={entry} index={index + 1} currency={draft.currency}
                initialOpen={!baseline?.draft.entries.some(saved => saved.id === entry.id)}
                invalid={Object.keys(visibleErrors).some(path => path.startsWith(`draft.entries[${index}]`))}
                gross={entry.discount ? calculation.result?.lines.find(line => line.id === entry.id)?.net : calculation.result?.lines.find(line => line.id === entry.id)?.gross}>
              <fieldset className="po-entry" aria-labelledby={`po-entry-title-${entry.id}`}>
                <div className="po-entry-heading">
                  <button className="quiet danger" type="button" aria-label={`Remove line ${index + 1}`} disabled={frozen} onClick={() => {
                    const entries = draft.entries.filter(old => old.id !== entry.id);
                    setRemovedEntries([...removedEntries, { entry, index }]);
                    addedEntry.current = entries[Math.min(index, entries.length - 1)]?.id ?? 'add-entry';
                    setErrors(Object.fromEntries(Object.entries(errors).filter(([path]) => !path.startsWith('draft.entries'))));
                    setDraft({ ...draft, entries });
                  }}>Remove line</button>
                </div>
                <DraftLineFields entry={entry} index={index + 1} errors={visibleErrors} disabled={frozen}
                  priceDisabled={frozen || currencyTransition} currency={draft.currency}
                  gross={calculation.result?.lines.find(line => line.id === entry.id)?.gross}
                  net={calculation.result?.lines.find(line => line.id === entry.id)?.net}
                  discountAmount={calculation.result?.lines.find(line => line.id === entry.id)?.discountAmount}
                  change={patch => setDraft({ ...draft, entries: draft.entries.map(old => old.id === entry.id ? { ...old, ...patch } : old) })} />
              </fieldset>
              </DraftLine>
            );
          })}
          <div className="po-add-entry">
            {removedEntries.length ? <div className="po-removal-recovery"><p role="status">Line removed. Undo is available until you save or change currency.</p><button className="quiet" type="button" disabled={frozen} onClick={() => {
              const removed = removedEntries[removedEntries.length - 1];
              const entries = [...draft.entries]; entries.splice(removed.index, 0, removed.entry);
              addedEntry.current = removed.entry.id;
              setRemovedEntries(removedEntries.slice(0, -1));
              setErrors(Object.fromEntries(Object.entries(errors).filter(([path]) => !path.startsWith('draft.entries'))));
              setDraft({ ...draft, entries });
            }}>Undo removal</button></div> : null}
            <button ref={addEntryButton} className="secondary" type="button" disabled={frozen} onClick={addLine}><Icon name="plus" />Add line</button>
          </div>
        </section>
        <section className="po-form-section" aria-labelledby="po-adjustments-heading">
          <div className="po-section-heading"><div><h2 id="po-adjustments-heading">Discounts and charges</h2><p>Apply order discounts to merchandise. Add shipping, taxes and other costs separately.</p></div></div>
          <DiscountFields label="Order discount" path="draft.orderDiscount" discount={draft.orderDiscount} base={calculation.result?.orderDiscountBase} amount={calculation.result?.orderDiscountAmount} currency={draft.currency} disabled={frozen || currencyTransition} errors={visibleErrors} change={orderDiscount => setDraft({ ...draft, orderDiscount })} />
          <DraftCharges draft={draft} baseline={baseline?.draft} disabled={frozen} amountDisabled={frozen || currencyTransition} errors={visibleErrors} change={charges => { setErrors(Object.fromEntries(Object.entries(errors).filter(([path]) => !path.startsWith('draft.charges')))); setDraft({ ...draft, charges }); }} />
          {draft.entries.length || draft.charges.length || draft.orderDiscount ? <div id="po-purchase-estimate" tabIndex={-1} className="po-merchandise-estimate" aria-live="polite">
            {calculation.result ? <DraftFinancialSummary draft={draft} result={calculation.result} /> : calculation.message ? <><p>{calculation.message}</p><button type="button" className="quiet" disabled={frozen} onClick={calculation.retry}>Retry estimate</button></> : <p>{frozen ? 'Estimates resume when editing is available.' : 'Calculating estimate…'}</p>}
          </div> : null}
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
