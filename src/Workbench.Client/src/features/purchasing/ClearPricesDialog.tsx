import { useEffect, useRef } from 'react';

export function ClearPricesDialog({ count, cancel, clear }: { count: number; cancel(): void; clear(): void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, []);
  return <dialog ref={dialog} aria-labelledby="po-clear-title" aria-describedby="po-clear-description" onCancel={event => { event.preventDefault(); cancel(); }}>
    <h2 id="po-clear-title">Clear reference prices?</h2>
    <p id="po-clear-description">This will clear {count} reference {count === 1 ? 'price' : 'prices'} and mark {count === 1 ? 'it' : 'them'} as unknown. Your entries and currency will stay the same. Changes are only saved when you save the draft.</p>
    <div className="button-row">
      <button type="button" className="primary" autoFocus onClick={cancel}>Cancel</button>
      <button type="button" className="secondary danger" onClick={clear}>Clear prices</button>
    </div>
  </dialog>;
}
