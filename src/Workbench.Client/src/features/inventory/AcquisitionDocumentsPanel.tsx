import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { ItemValidationError } from '../../api/items';
import { DocumentConflictError, changeDocument, downloadDocument, getDocumentOperation, getDocuments, uploadDocument, type AcquisitionDocument, type DocumentChange, type DocumentList, type DocumentOperation, type DocumentUpload } from '../../api/acquisitionDocuments';
import './acquisition-documents.css';
type Draft = { kind: 'upload' | 'rename' | 'remove'; document?: AcquisitionDocument };
type Command = { kind: 'upload'; payload: DocumentUpload } | { kind: 'rename' | 'remove'; documentId: string; payload: DocumentChange };
export function AcquisitionDocumentsPanel({ itemId, acquisitionId, itemVersion, acquisitionVersion, archived, disabled, onDirtyChange, onEditingChange, onAuthLost, onCurrent }: {
  itemId: string; acquisitionId: string; itemVersion: string; acquisitionVersion: string; archived: boolean; disabled: boolean;
  onDirtyChange(dirty: boolean, uncertain: boolean): void; onEditingChange(editing: boolean): void; onAuthLost(): void; onCurrent(itemVersion: string, acquisitionVersion: string): void | Promise<void>;
}) {
  const [list, setList] = useState<DocumentList>();
  const [attempt, setAttempt] = useState(0);
  const [failed, setFailed] = useState(false);
  const [draft, setDraft] = useState<Draft>();
  const [label, setLabel] = useState('');
  const [file, setFile] = useState<File>();
  const [command, setCommand] = useState<Command>();
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [downloadRetry, setDownloadRetry] = useState<AcquisitionDocument>();
  const heading = useRef<HTMLHeadingElement>(null);
  const alert = useRef<HTMLDivElement>(null);
  const labelInput = useRef<HTMLInputElement>(null);
  const active = useRef(true);
  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  useEffect(() => { if (error) alert.current?.focus(); }, [error]);
  useEffect(() => { if (draft) labelInput.current?.focus(); }, [draft]);
  useEffect(() => {
    if (!draft) return;
    onDirtyChange(true, Boolean(command)); onEditingChange(true);
    return () => { onDirtyChange(false, false); onEditingChange(false); };
  }, [draft, command, onDirtyChange, onEditingChange]);
  useEffect(() => {
    let current = true;
    void getDocuments(itemId, acquisitionId).then(result => {
      if (current) { setList(result); setFailed(false); }
    }, failure => {
      if (!current) return;
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) onAuthLost(); else setFailed(true);
    });
    return () => { current = false; };
  }, [itemId, acquisitionId, itemVersion, acquisitionVersion, attempt, onAuthLost]);
  function clear() {
    setDraft(undefined); setFile(undefined); setLabel(''); setCommand(undefined); setConflict(false); setError(''); heading.current?.focus();
  }
  function authLost(failure: unknown) {
    if (failure instanceof ApiError && [401, 403].includes(failure.status)) {
      clear(); setList(undefined); setDownloadRetry(undefined); onAuthLost(); return true;
    }
    return false;
  }
  async function result(outcome: DocumentOperation) {
    if (!active.current) return;
    if (outcome.state === 'Completed') {
      try {
        if (outcome.itemVersion && outcome.acquisitionVersion) await onCurrent(outcome.itemVersion, outcome.acquisitionVersion);
      } catch (failure) {
        if (active.current && !authLost(failure)) setError('The document change is saved, but current item and acquisition details could not be loaded. Check and retry before making another change.');
        return;
      }
      if (!active.current) return;
      clear(); setMessage('Document change saved.');
      setAttempt(value => value + 1);
    } else if (outcome.state === 'Conflict') {
      setConflict(true); setError('This acquisition changed. Review the current documents before starting a new change.');
    } else setError('This change is pending. Check and retry to resolve it before making another change.');
  }
  async function send(exact: Command, check = false) {
    setBusy(true); setError(''); setCommand(exact);
    try {
      if (check) {
        try {
          const status = await getDocumentOperation(itemId, acquisitionId, exact.payload.requestId);
          if (!active.current) return;
          if (status.state !== 'Pending') { await result(status); return; }
        } catch (failure) { if (!(failure instanceof ApiError && failure.status === 404)) throw failure; }
      }
      await result(exact.kind === 'upload' ? await uploadDocument(itemId, acquisitionId, exact.payload) : await changeDocument(itemId, acquisitionId, exact.documentId, exact.payload, exact.kind === 'remove'));
    } catch (failure) {
      if (!active.current) return;
      if (authLost(failure)) return;
      if (failure instanceof ItemValidationError) { setCommand(undefined); setError(Object.values(failure.errors).flat().join(' ')); }
      else if (failure instanceof ApiError && [404, 409].includes(failure.status)) {
        setConflict(true); setError(failure instanceof DocumentConflictError ? failure.reason : 'This acquisition changed or is no longer available. Review the current documents before starting a new change.');
      } else setError('We could not confirm this change. Your exact request is kept here. Check and retry before making another change.');
    } finally { if (active.current) setBusy(false); }
  }
  function submit() {
    if (!draft || busy || command) return;
    const payload = { requestId: crypto.randomUUID(), expectedItemVersion: list?.itemVersion ?? itemVersion, expectedAcquisitionVersion: list?.acquisitionVersion ?? acquisitionVersion, label: draft.kind === 'remove' ? null : label.trim() };
    if (draft.kind === 'upload' && file) void send({ kind: 'upload', payload: { ...payload, file } });
    else if (draft.document) void send({ kind: draft.kind === 'remove' ? 'remove' : 'rename', documentId: draft.document.id, payload: { ...payload, expectedDocumentVersion: draft.document.version } });
  }
  async function download(document: AcquisitionDocument) {
    setError(''); setDownloadRetry(undefined);
    try { await downloadDocument(itemId, acquisitionId, document); }
    catch (failure) { if (active.current && !authLost(failure)) { setDownloadRetry(document); setError('This file could not be downloaded. Retry the download; no document will be added.'); } }
  }
  function begin(next: Draft) { setDraft(next); setLabel(next.document?.label ?? ''); setMessage(''); setError(''); }
  async function discardAndReload() {
    setBusy(true);
    try {
      const current = await getDocuments(itemId, acquisitionId);
      if (!active.current) return;
      await onCurrent(current.itemVersion, current.acquisitionVersion);
      if (active.current) { clear(); setList(current); }
    } catch (failure) {
      if (active.current && !authLost(failure)) setError('Current item and acquisition details could not be loaded. Your draft is still here; retry reloading before making another change.');
    } finally { if (active.current) setBusy(false); }
  }
  const readOnly = archived || disabled;
  return <section className="acquisition-documents" aria-labelledby="documents-title">
    <h3 id="documents-title" ref={heading} tabIndex={-1}>Documents</h3>
    <p className="hint">Paperwork is shared by every piece linked to this acquisition. Collector-supplied evidence does not verify authenticity.</p>
    {archived ? <p className="hint">This item is archived. Documents are read-only.</p> : null}
    {message ? <p role="status">{message}</p> : null}
    {error ? <div role="alert" tabIndex={-1} ref={alert}><p>{error}</p>{downloadRetry ? <button className="secondary" onClick={() => void download(downloadRetry)}>Retry download</button> : null}</div> : null}
    {failed ? <div role="alert"><p>We could not load documents.</p><button className="secondary" onClick={() => { setFailed(false); setList(undefined); setAttempt(value => value + 1); }}>Retry loading documents</button></div> : !list ? <p role="status">Loading documents…</p> : <>
      {list.documents.length ? <ul className="document-list">{list.documents.map(document => <li key={document.id}>
        <div><strong>{document.label}</strong><p className="hint">{document.mediaType} · {document.length.toLocaleString()} bytes · Uploaded {new Date(document.createdAtUtc).toLocaleDateString()}</p>{document.unavailable ? <p>File unavailable after recovery. Contact your administrator.</p> : null}</div>
        <div className="document-actions"><button className="secondary" disabled={document.unavailable} aria-label={`Download ${document.label}`} onClick={() => void download(document)}>Download</button>
        {!archived ? <><button className="secondary" disabled={readOnly || Boolean(draft)} aria-label={`Rename ${document.label}`} onClick={() => begin({ kind: 'rename', document })}>Rename</button><button className="secondary" disabled={readOnly || Boolean(draft)} aria-label={`Remove ${document.label}`} onClick={() => begin({ kind: 'remove', document })}>Remove</button></> : null}</div>
      </li>)}</ul> : <p>No documents yet.</p>}
      {!archived && !draft ? <>{list.documents.length >= 20 ? <p>Document limit reached. Remove a document before adding another.</p> : null}<button className="secondary" disabled={readOnly || list.documents.length >= 20} onClick={() => begin({ kind: 'upload' })}>Add document</button></> : null}
    </>}
    {draft ? <form onSubmit={event => { event.preventDefault(); submit(); }} className="document-form">
      {draft.kind === 'remove' ? <p>Remove “{draft.document?.label}” from all linked pieces? Application access ends immediately; retained copies follow the seven-day retention policy and any holds.</p> : <label>Document label<input ref={labelInput} required maxLength={200} value={label} disabled={busy || Boolean(command)} onChange={event => setLabel(event.target.value)} /></label>}
      {draft.kind === 'upload' ? <><label>Choose document<input type="file" accept=".pdf,.jpg,.jpeg,.png,.webp" required disabled={busy || Boolean(command)} onChange={event => setFile(event.target.files?.[0])} /></label><p className="hint">PDF, JPEG, PNG or WebP, up to 10 MiB. Files are preserved as supplied and may contain embedded metadata. Format checks are not antivirus certification.</p></> : null}
      {busy ? <p role="status">Saving document change…</p> : null}
      <div className="document-actions">{command ? conflict ? <button type="button" className="secondary" disabled={busy} onClick={() => void discardAndReload()}>Discard request and reload documents</button> : <button type="button" disabled={busy} onClick={() => void send(command, true)}>Check and retry</button> : <button disabled={busy || (draft.kind !== 'remove' && !label.trim()) || (draft.kind === 'upload' && !file)}>{draft.kind === 'upload' ? 'Upload document' : draft.kind === 'rename' ? 'Save label' : 'Confirm removal'}</button>}
      {!command ? <button className="secondary" type="button" disabled={busy} onClick={() => { if ((!file && label === (draft.document?.label ?? '')) || window.confirm('Discard this document draft?')) clear(); }}>Cancel</button> : null}</div>
    </form> : null}
  </section>;
}
