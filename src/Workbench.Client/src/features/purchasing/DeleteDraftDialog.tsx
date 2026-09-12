import { useEffect, useRef } from 'react';

export function DeleteDraftDialog({ title, cancel, confirm }: { title: string; cancel(): void; confirm(): void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, []);
  return <dialog ref={dialog} aria-labelledby="po-delete-title" aria-describedby="po-delete-description"
    onCancel={event => { event.preventDefault(); cancel(); }}>
    <h2 id="po-delete-title">Delete draft?</h2>
    <p id="po-delete-description">Delete “{title}”? This removes the saved draft and discards any unsaved changes. This cannot be undone.</p>
    <div className="button-row">
      <button type="button" className="primary" autoFocus onClick={cancel}>Cancel</button>
      <button type="button" className="secondary danger" onClick={confirm}>Delete draft</button>
    </div>
  </dialog>;
}
