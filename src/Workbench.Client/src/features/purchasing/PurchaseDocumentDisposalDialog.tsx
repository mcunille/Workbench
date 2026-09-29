import { useEffect, useRef, useState } from 'react';
import type { PurchaseDocument } from '../../api/purchaseOrderDocuments';

export function PurchaseDocumentDisposalDialog({ document, initialReason = '', onConfirm, onClose, busy, error }: { document: PurchaseDocument; initialReason?: string; onConfirm: (reason: string) => void; onClose: () => void; busy: boolean; error: string | null }): React.JSX.Element {
  const [reason, setReason] = useState(initialReason);
  const dialog = useRef<HTMLDialogElement>(null);
  const reasonInput = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const previous = window.document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    reasonInput.current?.focus();
    return () => previous?.focus();
  }, []);
  useEffect(() => { if (error && !busy) reasonInput.current?.focus(); }, [error, busy]);
  return <dialog ref={dialog} aria-labelledby="po-dispose-title" aria-describedby="po-dispose-description" onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <h2 id="po-dispose-title">Dispose retained document?</h2>
    <p id="po-dispose-description">Dispose “{document.label}”? Its bytes become eligible for permanent removal after the file cleanup grace period. Accounting metadata and the disposal record remain.</p>
    <label htmlFor="po-dispose-reason">Reason for disposal<textarea id="po-dispose-reason" ref={reasonInput} maxLength={2000} required disabled={busy} value={reason} onChange={event => setReason(event.target.value)} /></label>
    {error ? <p role="alert" className="po-document-error">{error}</p> : null}
    <div className="button-row">
      <button type="button" className="secondary" disabled={busy} onClick={onClose}>Cancel</button>
      <button type="button" className="primary danger" disabled={busy || !reason.trim()} onClick={() => onConfirm(reason)}>Dispose document</button>
    </div>
  </dialog>;
}
