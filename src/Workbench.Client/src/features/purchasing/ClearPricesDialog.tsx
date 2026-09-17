import { useEffect, useRef, useState } from 'react';

export function ClearPricesDialog({ count, confirmedCharges = false, cancel, clear }: { count: number; confirmedCharges?: boolean; cancel(): void; clear(reason: string): void }) {
  const [reason, setReason] = useState('');
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, []);
  return <dialog ref={dialog} aria-labelledby="po-clear-title" aria-describedby="po-clear-description" onCancel={event => { event.preventDefault(); cancel(); }}>
    <h2 id="po-clear-title">Clear all amounts?</h2>
    <p id="po-clear-description">This clears {count} line {count === 1 ? 'price' : 'prices'}, including retained previous quotes, all discounts and all charge amounts. Prices and charge amounts become unknown; charge status becomes estimated. Quantities, units, charge descriptions and currency stay the same. Save the draft to keep this change.</p>
    {confirmedCharges ? <label htmlFor="po-clear-reason">Reason for clearing confirmed charges<textarea id="po-clear-reason" value={reason} onChange={event => setReason(event.target.value)} /><span>This explanation will be appended to each affected charge’s notes.</span></label> : null}
    <div className="button-row">
      <button type="button" className="primary" autoFocus onClick={cancel}>Cancel</button>
      <button type="button" className="secondary danger" disabled={confirmedCharges && !reason.trim()} onClick={() => clear(reason)}>Clear amounts</button>
    </div>
  </dialog>;
}
