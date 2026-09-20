import { useEffect, useRef, useState } from 'react';
import { FloatingField } from '../../FloatingField';
import { ApiError } from '../../api/auth';
import { ItemValidationError } from '../../api/items';
import { changePurchaseDocument, downloadPurchaseDocument, getPurchaseDocumentOperation, getPurchaseDocuments, PurchaseDocumentConflict, uploadPurchaseDocument, type PurchaseDocument, type PurchaseDocumentChange, type PurchaseDocumentList, type PurchaseDocumentUpload } from '../../api/purchaseOrderDocuments';
import './purchase-documents.css';

type FileDraft = { id: string; file: File; label: string; saved: boolean; error?: string };
type Editor = { kind: 'upload' } | { kind: 'rename' | 'remove'; document: PurchaseDocument };
type Command = { kind: 'upload'; key: string; payload: PurchaseDocumentUpload } | { kind: 'rename' | 'remove'; documentId: string; payload: PurchaseDocumentChange };
type Phase = 'ready' | 'uncertain' | 'conflict' | 'refresh';
type Props = { orderId: string; onAuthLost(): void; onCurrent(): Promise<void>; onStateChange(dirty: boolean, uncertain: boolean, editing: boolean): void };

function fileSize(value: number | string) { const bytes = Number(value); return bytes < 1024 * 1024 ? `${Math.max(1, Math.ceil(bytes / 1024)).toLocaleString()} KiB` : `${(bytes / (1024 * 1024)).toFixed(1)} MiB`; }
function initialLabel(file: File) { return (file.name.split(/[\\/]/).pop()?.replace(/[\u0000-\u001f\u007f]/g, '').trim() || 'Invoice file').slice(0, 200); }

export function PurchaseDocuments({ orderId, onAuthLost, onCurrent, onStateChange }: Props) {
  const [list, setList] = useState<PurchaseDocumentList>();
  const [loadFailed, setLoadFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [editor, setEditor] = useState<Editor>();
  const [queue, setQueue] = useState<FileDraft[]>([]);
  const [label, setLabel] = useState('');
  const [command, setCommand] = useState<Command>();
  const [phase, setPhase] = useState<Phase>('ready');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [downloadRetry, setDownloadRetry] = useState<PurchaseDocument>();
  const active = useRef(true);
  const inFlight = useRef(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const errorRegion = useRef<HTMLDivElement>(null);
  const editorHeading = useRef<HTMLHeadingElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  const uncertain = phase === 'uncertain' || phase === 'refresh' || busy;
  const dirty = (editor?.kind === 'upload' && queue.some(item => !item.saved)) || (editor?.kind === 'rename' && label !== editor.document.label) || uncertain;

  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  useEffect(() => { onStateChange(dirty, uncertain, !!editor); }, [dirty, uncertain, editor, onStateChange]);
  useEffect(() => () => onStateChange(false, false, false), [onStateChange]);
  useEffect(() => { if (error) errorRegion.current?.focus(); }, [error]);
  useEffect(() => { if (editor) editorHeading.current?.focus(); }, [editor]);
  useEffect(() => {
    let current = true;
    void getPurchaseDocuments(orderId).then(value => { if (current) { setList(value); setLoadFailed(false); } }, failure => {
      if (!current) return;
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) onAuthLost();
      else setLoadFailed(true);
    });
    return () => { current = false; };
  }, [orderId, attempt, onAuthLost]);

  function close() {
    setEditor(undefined); setQueue([]); setLabel(''); setCommand(undefined); setPhase('ready'); setError('');
    requestAnimationFrame(() => { if (active.current) (addButton.current && !addButton.current.disabled ? addButton.current : heading.current)?.focus(); });
  }
  function lostAccess(failure: unknown) {
    if (!(failure instanceof ApiError && [401, 403].includes(failure.status))) return false;
    close(); setList(undefined); setDownloadRetry(undefined); setMessage(''); onAuthLost(); return true;
  }
  async function refresh(): Promise<PurchaseDocumentList> {
    const value = await getPurchaseDocuments(orderId);
    if (!active.current) return value;
    await onCurrent();
    if (active.current) { setList(value); setLoadFailed(false); }
    return value;
  }
  async function confirm(exact: Command): Promise<PurchaseDocumentList | undefined> {
    // Once the command is confirmed, subsequent failures may retry reads only.
    setPhase('refresh');
    if (exact.kind === 'upload') setQueue(items => items.map(item => item.id === exact.key ? { ...item, saved: true, error: undefined } : item));
    setMessage(exact.kind === 'upload' ? 'File uploaded.' : exact.kind === 'rename' ? 'File label saved.' : 'File removed.');
    try {
      const current = await refresh();
      if (!active.current) return;
      setCommand(undefined); setPhase('ready');
      if (exact.kind !== 'upload') close();
      return current;
    } catch (failure) {
      if (active.current && !lostAccess(failure)) setError('The file change is saved, but current purchase details could not be loaded. Retry loading; do not upload the file again.');
    }
  }
  async function execute(exact: Command, check = false): Promise<PurchaseDocumentList | undefined> {
    setCommand(exact); setError('');
    try {
      if (check) {
        try {
          const status = await getPurchaseDocumentOperation(orderId, exact.payload.requestId);
          if (!active.current) return;
          if (status.state === 'Completed') return await confirm(exact);
          if (status.state === 'Conflict') throw new PurchaseDocumentConflict('The purchase or file changed. Review current files before retrying.');
        } catch (failure) { if (!(failure instanceof ApiError && failure.status === 404)) throw failure; }
      }
      const result = exact.kind === 'upload' ? await uploadPurchaseDocument(orderId, exact.payload) : await changePurchaseDocument(orderId, exact.documentId, exact.payload, exact.kind === 'remove');
      if (!active.current) return;
      if (result.state === 'Completed') return await confirm(exact);
      if (result.state === 'Conflict') throw new PurchaseDocumentConflict('The purchase or file changed. Review current files before retrying.');
      setPhase('uncertain'); setError('This file change is pending. Check and retry before making another change.');
    } catch (failure) {
      if (!active.current || lostAccess(failure)) return;
      if (failure instanceof ItemValidationError) {
        const reason = Object.values(failure.errors).flat().join(' ');
        setCommand(undefined); setPhase('ready'); setError(reason);
        if (exact.kind === 'upload') setQueue(items => items.map(item => item.id === exact.key ? { ...item, error: reason } : item));
      } else if (failure instanceof ApiError && [404, 409].includes(failure.status)) {
        setPhase('conflict'); setError(failure instanceof PurchaseDocumentConflict ? failure.reason : 'The purchase or file is no longer available. Review current files before retrying.');
      } else { setPhase('uncertain'); setError('This file change could not be confirmed. Your exact request is kept here. Check and retry before making another change.'); }
    }
  }
  async function run(action: () => Promise<unknown>) {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setError('');
    try { await action(); }
    finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  async function submit() {
    if (!editor || !list || command) return;
    await run(async () => {
      if (editor.kind === 'upload') {
        let current = list;
        for (const item of queue.filter(item => !item.saved)) {
          const next = await execute({ kind: 'upload', key: item.id, payload: { requestId: crypto.randomUUID(), expectedOrderVersion: current.orderVersion, label: item.label.trim(), file: item.file } });
          if (!next || !active.current) return;
          current = next;
        }
        setMessage('Selected files uploaded.');
      } else await execute({ kind: editor.kind, documentId: editor.document.id, payload: { requestId: crypto.randomUUID(), expectedOrderVersion: list.orderVersion, expectedDocumentVersion: editor.document.version, label: editor.kind === 'remove' ? null : label.trim() } });
    });
  }
  async function reviewConflict() {
    await run(async () => {
      try {
        const current = await refresh();
        if (!active.current) return;
        if (editor && editor.kind !== 'upload') {
          const document = current.documents.find(item => item.id === editor.document.id);
          if (!document) { close(); setMessage('That file is no longer attached. Current files are shown.'); return; }
          setEditor({ ...editor, document });
        }
        setCommand(undefined); setPhase('ready'); setMessage('Current files loaded. Review them, then explicitly submit your remaining change.');
      } catch (failure) { if (active.current && !lostAccess(failure)) setError('Current files could not be loaded. Your selection is kept; retry reviewing current files.'); }
    });
  }
  async function download(document: PurchaseDocument) {
    setDownloadRetry(undefined); setError('');
    try { await downloadPurchaseDocument(orderId, document); }
    catch (failure) { if (active.current && !lostAccess(failure)) { setDownloadRetry(document); setError(failure instanceof ApiError && failure.status === 410 ? 'This file is unavailable after recovery. Contact your administrator or add another copy.' : 'This file could not be downloaded. Retry the download; no file will be added.'); } }
  }
  function begin(next: Editor) { setEditor(next); setLabel(next.kind === 'upload' ? '' : next.document.label); setError(''); setMessage(''); setDownloadRetry(undefined); }
  const remaining = queue.filter(item => !item.saved);
  const frozen = busy || !!command;
  return <section className="po-documents" aria-labelledby="po-documents-title">
    <div className="po-documents-heading"><h2 id="po-documents-title" ref={heading} tabIndex={-1}>Invoice files</h2>
      {!editor && list ? <button ref={addButton} type="button" className="secondary" disabled={list.documents.length >= 20 || loadFailed} onClick={() => begin({ kind: 'upload' })}>Add invoice files</button> : null}</div>
    {message ? <p role="status">{message}</p> : null}
    {error ? <div role="alert" ref={errorRegion} tabIndex={-1} className="po-document-error"><p>{error}</p>{downloadRetry ? <button type="button" className="secondary" onClick={() => void download(downloadRetry)}>Retry download</button> : null}</div> : null}
    {loadFailed ? <div role="alert"><p>Invoice files could not be loaded. The purchase is still available.</p><button type="button" className="secondary" onClick={() => { setLoadFailed(false); setAttempt(value => value + 1); }}>Retry loading files</button></div> : !list ? <p role="status">Loading invoice files…</p> : null}
    {list ? <>
      {list.documents.length ? <ul className="po-document-list">{list.documents.map(document => <li key={document.id}>
        <div className="po-document-description"><strong>{document.label}</strong><p>{document.extension.toUpperCase()} · {fileSize(document.length)} · Uploaded <time dateTime={document.createdAtUtc}>{new Date(document.createdAtUtc).toLocaleDateString()}</time></p>
          {document.unavailable ? <p>File unavailable after recovery. Contact your administrator or add another copy.</p> : null}</div>
        <div className="po-document-actions"><button type="button" className="secondary" disabled={document.unavailable || busy} aria-label={`Download ${document.label}`} onClick={() => void download(document)}>Download</button>
          <button type="button" className="quiet" disabled={!!editor || busy || loadFailed} aria-label={`Rename ${document.label}`} onClick={() => begin({ kind: 'rename', document })}>Rename</button>
          <button type="button" className="quiet danger" disabled={!!editor || busy || loadFailed} aria-label={`Remove ${document.label}`} onClick={() => begin({ kind: 'remove', document })}>Remove</button></div>
      </li>)}</ul> : !loadFailed ? <p className="hint">Keep supplier invoices and supporting purchase paperwork here. Add one or more PDF files; no invoice details are required.</p> : null}
      {list.documents.length >= 20 ? <p className="hint">20-file limit reached. Remove a file before adding another.</p> : null}
    </> : null}
    {editor ? <form className="po-document-form" onSubmit={event => { event.preventDefault(); void submit(); }}>
      <h3 ref={editorHeading} tabIndex={-1}>{editor.kind === 'upload' ? 'Add invoice files' : editor.kind === 'rename' ? 'Rename file' : 'Remove file'}</h3>
      {editor.kind === 'upload' ? <>
        {!queue.length ? <label className="po-file-picker">Choose files<input type="file" multiple accept=".pdf,.jpg,.jpeg,.png,.webp" disabled={frozen} onChange={event => {
          const files = Array.from(event.target.files ?? []);
          setQueue(files.map(file => ({ id: crypto.randomUUID(), file, label: initialLabel(file), saved: false })));
          setError(files.length + (list?.documents.length ?? 0) > 20 ? 'Choose fewer files: a purchase can have up to 20 current or pending files.' : '');
        }} /></label> : null}
        <p className="hint">PDF, JPEG, PNG or WebP, up to 10 MiB each. Files stay private to your business and may contain embedded metadata. Only uploaded files survive leaving this page.</p>
        <ul className="po-file-queue">{queue.map((item, index) => <li key={item.id}>
          <div>{item.saved ? <strong>{item.label}</strong> : <FloatingField htmlFor={`po-file-label-${item.id}`} label={`File label ${index + 1}`}><input id={`po-file-label-${item.id}`} placeholder=" " maxLength={200} required disabled={frozen} value={item.label} onChange={event => setQueue(items => items.map(row => row.id === item.id ? { ...row, label: event.target.value, error: undefined } : row))} /></FloatingField>}
            <p className="hint">{item.file.name} · {fileSize(item.file.size)} · {item.saved ? 'Uploaded' : busy && command?.kind === 'upload' && command.key === item.id ? 'Uploading…' : 'Not uploaded'}</p>
            {item.error ? <p className="error-text">{item.error}</p> : null}
            {item.file.size > 10 * 1024 * 1024 ? <p className="error-text">This file exceeds 10 MiB. Remove it and choose a smaller copy.</p> : null}</div>
          {!item.saved ? <button type="button" className="quiet" disabled={frozen} aria-label={`Remove selected file ${index + 1}`} onClick={() => setQueue(items => items.filter(row => row.id !== item.id))}>Remove from selection</button> : null}
        </li>)}</ul>
      </> : editor.kind === 'rename' ? <FloatingField htmlFor="po-document-label" label="File label"><input id="po-document-label" placeholder=" " required maxLength={200} disabled={frozen} value={label} onChange={event => setLabel(event.target.value)} /></FloatingField> : <p>Remove “{editor.document.label}” from this purchase? Download access ends immediately. Retained copies follow the seven-day retention policy and any holds.</p>}
      {busy ? <p role="status">Saving file changes…</p> : null}
      <div className="po-document-actions">
        {command ? <button type="button" className="primary" disabled={busy} onClick={() => phase === 'conflict' ? void reviewConflict() : void run(() => phase === 'refresh' ? confirm(command) : execute(command, true))}>{phase === 'refresh' ? 'Retry loading saved files' : phase === 'conflict' ? 'Review current files' : 'Check and retry file'}</button> : editor.kind === 'upload' && queue.length > 0 && !remaining.length ? <button type="button" className="primary" onClick={close}>Done</button> : <button type="submit" className="primary" disabled={busy || !list || (editor.kind === 'upload' ? !remaining.length || remaining.some(item => !item.label.trim() || item.file.size > 10 * 1024 * 1024) || remaining.length + list.documents.length > 20 : editor.kind === 'rename' && !label.trim())}>{editor.kind === 'upload' ? queue.some(item => item.saved) ? 'Upload remaining files' : 'Upload files' : editor.kind === 'rename' ? 'Save label' : 'Confirm removal'}</button>}
        {!command && !(editor.kind === 'upload' && queue.length > 0 && !remaining.length) ? <button type="button" className="secondary" disabled={busy} onClick={() => { if (!dirty || window.confirm('Discard unsaved file changes? Files already uploaded will stay attached.')) close(); }}>Cancel</button> : null}
      </div>
    </form> : null}
  </section>;
}
