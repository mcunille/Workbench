import { RecoveryText } from '../../RecoveryText';
import { recoveryText } from '../../formatRecoveryText';
import { useEffect, useId, useRef, useState } from 'react';
import { Icon } from '../../Icon';
import { FloatingField } from '../../FloatingField';
import { ApiError } from '../../api/auth';
import { getSupplier, createSupplier, updateSupplier, archiveSupplier, SupplierError, type Supplier, type SupplierContent, type SupplierReceipt, type CreateSupplierRequest, type UpdateSupplierRequest, type ArchiveSupplierRequest } from '../../api/suppliers';
import { SupplierDetails } from './supplierDetails';
import { emptySupplier, supplierFields } from './supplierSnapshot';
import { SupplierDialog } from './SupplierDialog';
import './purchasing.css';
type Mode = 'loading' | 'editing' | 'saving' | 'uncertain' | 'reading' | 'read-failed' | 'comparison' | 'blocked' | 'load-failed';
type Submission = { kind: 'save'; id?: string; body: CreateSupplierRequest | UpdateSupplierRequest } | { kind: 'archive'; id: string; body: ArchiveSupplierRequest };
interface Props { id?: string; inline?: boolean; onDirtyChange(dirty: boolean, uncertain: boolean): void; onAuthLost(): void; onCancel(): void; onCreated?(id: string): void; onSelected?(supplier: Supplier): void; }
export function SupplierEditor({ id: initialId, inline, onDirtyChange, onAuthLost, onCancel, onCreated, onSelected }: Props) {
  const formId = useId();
  const toolbarStart = useRef<HTMLDivElement>(null);
  const [toolbarPinned, setToolbarPinned] = useState(false);
  useEffect(() => {
    const marker = toolbarStart.current;
    if (!marker || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(([entry]) => {
      setToolbarPinned(!entry.isIntersecting && entry.boundingClientRect.top < 0);
    });
    observer.observe(marker);
    return () => observer.disconnect();
  }, [inline]);
  const [supplier, setSupplier] = useState<SupplierContent>(emptySupplier);
  const [baseline, setBaseline] = useState<Supplier>();
  const [current, setCurrent] = useState<Supplier>();
  const [id, setId] = useState(initialId);
  const [mode, setMode] = useState<Mode>(initialId ? 'loading' : 'editing');
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [confirmArchive, setConfirmArchive] = useState(false);
  const [pendingArchive, setPendingArchive] = useState<boolean>();
  const active = useRef(true); const busy = useRef(false);
  const submission = useRef<Submission | undefined>(undefined);
  const receipt = useRef<SupplierReceipt | undefined>(undefined);
  const [readReason, setReadReason] = useState<'initial' | 'saved' | 'conflict'>('initial');
  const firstId = useRef(initialId);
  const changed = JSON.stringify(supplier) !== JSON.stringify(baseline?.supplier ?? emptySupplier());
  const uncertain = mode === 'saving' || mode === 'uncertain';
  const dirty = uncertain || mode === 'comparison' || (changed && (mode === 'editing' || mode === 'blocked' || ((mode === 'read-failed' || mode === 'reading') && readReason === 'conflict')));
  useEffect(() => { onDirtyChange(dirty, uncertain); }, [dirty, uncertain, onDirtyChange]);
  useEffect(() => () => onDirtyChange(false, false), [onDirtyChange]);
  useEffect(() => {
    active.current = true; let valid = true;
    if (firstId.current) void getSupplier(firstId.current).then(value => { if (active.current && valid) { setSupplier(value.supplier); setBaseline(value); setMode('editing'); } }, error => {
      if (!active.current || !valid) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) onAuthLost();
      const unavailable = error instanceof ApiError && error.status === 404;
      setMode(unavailable ? 'blocked' : 'load-failed'); setMessage(unavailable ? 'This supplier is unavailable. Return to suppliers to choose another.' : 'Couldn’t load this supplier. Try loading it again.');
    });
    return () => { active.current = false; valid = false; };
  }, [onAuthLost]);
  useEffect(() => { const key = Object.keys(errors)[0]; if (key) document.getElementById(`supplier-${key.replace('supplier.', '')}`)?.focus(); }, [errors]);
  function accessFailure(error: unknown) {
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
      setSupplier(emptySupplier()); setBaseline(undefined); setCurrent(undefined); submission.current = undefined; receipt.current = undefined; setMode('blocked'); onDirtyChange(false, false); onAuthLost(); return true;
    }
    if (error instanceof ApiError && error.status === 404) { setMode('blocked'); setMessage('This supplier is unavailable. Your input is kept.'); return true; }
    return false;
  }
  async function read(target: string, reason: 'initial' | 'saved' | 'conflict', saved?: SupplierReceipt) {
    setReadReason(reason); setMode('reading');
    try {
      const latest = await getSupplier(target); if (!active.current) return;
      if (reason === 'conflict' || (saved && latest.version !== saved.savedVersion)) { setCurrent(latest); setMode('comparison'); return; }
      setSupplier(latest.supplier); setBaseline(latest); setCurrent(undefined); submission.current = undefined; receipt.current = undefined; setMode('editing');
      setMessage(reason === 'saved' && inline ? 'Supplier saved. Existing orders keep their saved details.' : '');
      if (reason === 'saved' && inline) onSelected?.(latest);
    } catch (error) { if (!active.current || accessFailure(error)) return; setMode('read-failed'); setMessage(reason === 'saved' ? 'Supplier saved; current details could not be loaded.' : 'Your input is kept; current supplier details could not be loaded.'); }
  }
  async function execute(archive = false) {
    if (busy.current || !['editing', 'uncertain'].includes(mode)) return;
    const request = submission.current ?? (archive && id && baseline
      ? { kind: 'archive' as const, id, body: { requestId: crypto.randomUUID(), expectedVersion: baseline.version, isArchived: !baseline.isArchived } }
      : { kind: 'save' as const, id, body: baseline ? { requestId: crypto.randomUUID(), expectedVersion: baseline.version, supplier: structuredClone(supplier) } : { requestId: crypto.randomUUID(), supplier: structuredClone(supplier) } });
    submission.current = request; busy.current = true; setPendingArchive(request.kind === 'archive' ? request.body.isArchived : undefined); setConfirmArchive(false); setMode('saving'); setErrors({}); setMessage('');
    try {
      const saved = request.kind === 'archive' ? await archiveSupplier(request.id, request.body) : request.id ? await updateSupplier(request.id, request.body as UpdateSupplierRequest) : await createSupplier(request.body);
      if (!active.current) return; receipt.current = saved; submission.current = undefined; setId(saved.supplierId);
      if (!request.id) onCreated?.(saved.supplierId);
      await read(saved.supplierId, 'saved', saved);
    } catch (error) {
      if (!active.current || accessFailure(error)) return;
      if (error instanceof SupplierError && error.code === 'supplier_version_conflict' && request.id) { submission.current = undefined; await read(request.id, 'conflict'); }
      else if (error instanceof SupplierError && error.code === 'supplier_request_conflict') { setMode('blocked'); setMessage('We couldn’t safely apply these changes. Your input is kept. Reopen the supplier to review the saved details.'); }
      else if (error instanceof ApiError && error.status >= 400 && error.status < 500) { submission.current = undefined; setMode('editing'); setErrors(error instanceof SupplierError ? error.errors : {}); setMessage('Review the supplier fields and save again.'); }
      else { setMode('uncertain'); setMessage('We couldn’t confirm whether this change was saved. Your input is kept. Retry to check the result.'); }
    } finally { busy.current = false; }
  }
  function reconcile(keep: boolean) {
    if (!current) return; setBaseline(current); if (!keep) setSupplier(current.supplier); setCurrent(undefined); receipt.current = undefined; submission.current = undefined; setMode('editing'); setMessage('Review the details, then save your changes.');
  }
  const frozen = mode !== 'editing';
  function field([key, label, limit]: typeof supplierFields[number]) {
    const controlId = `supplier-${key}`; const error = errors[`supplier.${key}`]?.join(' ');
    const description = [key === 'name' ? `${formId}-required` : '', error ? `${controlId}-error` : ''].filter(Boolean).join(' ') || undefined;
    const common = { id: controlId, value: supplier[key] ?? '', disabled: frozen, maxLength: limit, placeholder: ' ', 'aria-invalid': !!error, 'aria-describedby': description, onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setSupplier({ ...supplier, [key]: event.target.value || (key === 'name' ? '' : null) }) };
    return <div className={`po-field${key === 'postalAddress' ? ' po-supplier-address' : ''}`} key={key}>
      <FloatingField htmlFor={controlId} label={key === 'name' ? 'Supplier name' : label}>{key === 'postalAddress' ? <textarea {...common} rows={3} /> : <input {...common} type={key === 'email' ? 'email' : key === 'phone' ? 'tel' : key === 'website' ? 'url' : 'text'} required={key === 'name'} />}</FloatingField>
      {key === 'name' ? <p className="po-field-help po-required-help" id={`${formId}-required`}>Required</p> : null}
      {error ? <p className="form-message error" id={`${controlId}-error`}>{error}</p> : null}
    </div>;
  }
  const savingLabel = pendingArchive === true ? 'Archiving supplier…' : pendingArchive === false ? 'Reactivating supplier…' : 'Saving supplier…';
  const retryLabel = pendingArchive === true ? 'Retry archive' : pendingArchive === false ? 'Retry reactivation' : 'Retry save';
  const saveLabel = mode === 'saving' ? savingLabel : mode === 'uncertain' ? retryLabel : 'Save supplier';
  const saveButton = <button className="primary" type="submit" form={formId} disabled={(mode !== 'editing' && mode !== 'uncertain') || (mode === 'editing' && !!baseline && !changed && !inline)}>{saveLabel}</button>;
  const saveStatus = mode === 'saving' ? savingLabel
    : mode === 'loading' || mode === 'reading' ? 'Loading current supplier…'
    : mode === 'uncertain' ? 'Change not confirmed'
    : baseline ? `Saved ${new Date(baseline.updatedAtUtc).toLocaleString()}${dirty ? ' · Unsaved changes' : ''}`
    : changed ? 'Unsaved changes' : '';
  return <section className={inline ? 'po-supplier-inline-editor' : 'editor po-editor po-supplier-editor'} aria-label="Supplier editor">
    {!inline ? <>
      <div ref={toolbarStart} className="po-toolbar-start" aria-hidden="true" />
      <div className={`po-editor-toolbar${toolbarPinned ? ' is-pinned' : ''}`}>
        <button className="quiet po-back" type="button" aria-label="Back to suppliers" onClick={onCancel}><Icon name="back" />Suppliers</button>
        {saveButton}
      </div>
      <header className="po-editor-header">
        <div className="po-heading"><h1>{baseline?.supplier.name ?? (id ? 'Supplier' : 'New supplier')}</h1>{baseline?.isArchived ? <span className="po-badge">Archived</span> : null}</div>
        {saveStatus ? <p className="po-save-status" role="status">{saveStatus}</p> : null}
      </header>
    </> : null}
    {inline && baseline?.isArchived ? <p className="po-badge">Archived supplier</p> : null}
    {message ? <p role={mode === 'editing' && !Object.keys(errors).length ? 'status' : 'alert'}>{message}</p> : null}
    {inline && (mode === 'loading' || mode === 'reading') ? <p role="status">Loading current supplier…</p> : null}
    {mode === 'read-failed' || mode === 'load-failed' ? <button type="button" className="secondary" onClick={() => { if (id && !busy.current) { busy.current = true; void read(id, readReason, receipt.current).finally(() => { busy.current = false; }); } }}>Load current supplier</button> : null}
    {current && mode === 'comparison' ? <section className="po-comparison-panel" aria-label="Compare supplier versions"><h2>Review newer supplier changes</h2><div className="po-comparison"><SupplierDetails heading="Current saved" supplier={current.supplier} archived={current.isArchived} /><SupplierDetails heading="Your changes" supplier={supplier} archived={baseline?.isArchived} /></div><div className="button-row"><button className="secondary" type="button" onClick={() => reconcile(false)}>Use saved version</button><button className="primary" type="button" onClick={() => reconcile(true)}>Continue with my changes</button></div></section> : null}
    {['uncertain', 'read-failed', 'comparison', 'blocked'].includes(mode) && recoveryText(supplier) ? <RecoveryText label="Supplier" text={recoveryText(supplier)} /> : null}
    <form id={formId} className="form-stack" noValidate onSubmit={event => { event.preventDefault(); event.stopPropagation(); void execute(); }}>
      {Object.keys(errors).length ? <div className="po-validation-summary" role="alert"><h2>Review these fields</h2><ul>{Object.entries(errors).flatMap(([path, values]) => values.map((value, index) => <li key={`${path}-${index}`}><a href={`#supplier-${path.replace('supplier.', '')}`} onClick={event => { event.preventDefault(); document.getElementById(`supplier-${path.replace('supplier.', '')}`)?.focus(); }}>{value}</a></li>))}</ul></div> : null}
      <div className="po-form-section">{field(supplierFields[0])}</div>
      <section className="po-form-section" role="group" aria-labelledby={`${formId}-contacts`}>
        <div className="po-section-heading"><div><h2 id={`${formId}-contacts`}>Contact details (optional)</h2><p>Changes here won’t update existing purchase orders.</p></div></div>
        <div className="po-supplier-contact-fields">{supplierFields.slice(1).map(field)}</div>
      </section>
      {inline ? <div className="button-row po-dialog-footer"><button className="secondary" type="button" onClick={onCancel}>Cancel</button>{saveButton}
      {inline && baseline && mode === 'editing' && !changed ? <button className="secondary" type="button" onClick={() => onSelected?.(baseline)}>Select saved supplier</button> : null}
      </div> : null}
    </form>
    {baseline && !inline ? <button className="quiet danger" type="button" disabled={frozen || changed} onClick={() => setConfirmArchive(true)}>{baseline.isArchived ? 'Reactivate supplier' : 'Archive supplier'}</button> : null}
    {baseline && changed && !inline ? <p className="po-field-help">Save your edits before archiving or reactivating this supplier.</p> : null}
    {confirmArchive && baseline ? <SupplierDialog title={baseline.isArchived ? 'Reactivate supplier?' : 'Archive supplier?'} cancel={() => setConfirmArchive(false)}><p>{baseline.isArchived ? 'This supplier will be available for new orders.' : 'This removes the supplier from new selections. Existing orders and their saved details remain.'}</p><div className="button-row"><button className="secondary" type="button" autoFocus onClick={() => setConfirmArchive(false)}>Cancel</button><button className="primary" type="button" onClick={() => void execute(true)}>{baseline.isArchived ? 'Confirm reactivation' : 'Confirm archive'}</button></div></SupplierDialog> : null}
  </section>;
}
