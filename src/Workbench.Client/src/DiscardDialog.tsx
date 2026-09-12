import { useEffect, useRef } from 'react';
export function DiscardDialog({
  uncertain,
  keep,
  discard,
}: {
  uncertain: boolean;
  keep(): void;
  discard(): void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, []);
  return (
    <dialog
      ref={dialog}
      aria-labelledby="discard-title"
      onCancel={(event) => {
        event.preventDefault();
        keep();
      }}
    >
      <h2 id="discard-title">Discard changes?</h2>
      <p>
        {uncertain
          ? 'Your save may already have completed. Leaving cannot undo it and loses the retry request kept in memory. Check the saved record before making another change.'
          : 'Your unsaved changes will be discarded.'}
      </p>
      <div className="button-row">
        <button className="primary" autoFocus onClick={keep}>
          Keep editing
        </button>
        <button className="secondary danger" onClick={discard}>
          Discard changes
        </button>
      </div>
    </dialog>
  );
}
