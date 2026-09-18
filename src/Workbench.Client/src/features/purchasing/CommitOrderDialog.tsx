import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { commitOrder, DraftError, type CommitOrderRequest, type DraftOrder, type OrderReceipt } from '../../api/purchaseOrders';
import { SupplierDialog } from './SupplierDialog';
import { DraftComparison } from './DraftComparison';
import { RecoveryText } from '../../RecoveryText';
import { recoveryText } from '../../formatRecoveryText';

export function CommitOrderDialog({ order, date, changeDate, editDraft, cancel, committed, conflict, onAuthLost, pending }: {
  order: DraftOrder; cancel(): void; committed(receipt: OrderReceipt): void; conflict(): void;
  date: string; changeDate(value: string): void; editDraft(errors: Record<string, string[]>): void;
  onAuthLost(): void; pending(value: boolean): void;
}) {
  const [mode, setMode] = useState<'editing' | 'saving' | 'uncertain' | 'blocked'>('editing');
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const dateInput = useRef<HTMLInputElement>(null);
  useEffect(() => { if (errors.orderDate) dateInput.current?.focus(); }, [errors]);
  const request = useRef<CommitOrderRequest | undefined>(undefined);
  const busy = useRef(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  async function confirm() {
    if (busy.current || mode === 'blocked') return;
    if (!date) { setErrors({ orderDate: ['Enter the date you placed the order.'] }); return; }
    busy.current = true;
    request.current ??= { requestId: crypto.randomUUID(), expectedVersion: order.version, orderDate: date };
    setMode('saving'); setMessage(''); setErrors({}); pending(true);
    try {
      const receipt = await commitOrder(order.id, request.current);
      if (!alive.current) return;
      pending(false); committed(receipt);
    } catch (error) {
      if (!alive.current) return;
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) { pending(false); onAuthLost(); return; }
      if (error instanceof DraftError && error.code === 'purchase_version_conflict') { pending(false); conflict(); return; }
      if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
        request.current = undefined; pending(false);
        setMode(error.status === 404 || error.status === 409 ? 'blocked' : 'editing');
        setMessage(error.status === 409 ? 'This purchase has changed. Close this review and reload the purchase before continuing.' : error.status === 404 ? 'This purchase is unavailable.' : 'Review the order details. Close this review to correct and save the draft.');
        setErrors(error instanceof DraftError ? error.errors : {});
      } else { setMode('uncertain'); setMessage('We couldn’t confirm the commitment. Check and retry with the same request.'); }
    } finally { busy.current = false; }
  }
  const locked = mode !== 'editing';
  return <SupplierDialog title="Record as ordered" cancel={() => { if (mode !== 'saving' && mode !== 'uncertain') cancel(); }}>
    <p>Record the saved contents of {order.poReference} as the purchase you placed. Prices remain estimates and unknown costs stay unknown.</p>
    <label htmlFor="po-commit-date">Order date</label>
    <input ref={dateInput} id="po-commit-date" type="date" value={date} disabled={locked} aria-invalid={!!errors.orderDate} aria-describedby={errors.orderDate ? 'po-commit-date-error' : undefined} onChange={event => changeDate(event.target.value)} />
    {errors.orderDate ? <p id="po-commit-date-error" className="form-message error" role="alert">{errors.orderDate.join(' ')}</p> : null}
    <details><summary>Review saved contents</summary><DraftComparison heading="Agreed contents" draft={order.draft} /></details>
    {message ? <p role="alert">{message}</p> : null}
    {Object.keys(errors).some(key => key !== 'orderDate') ? <ul>{Object.entries(errors).filter(([key]) => key !== 'orderDate').flatMap(([key, messages]) => messages.map((error, index) => <li key={`${key}-${index}`}><button type="button" className="quiet" onClick={() => editDraft({ [key]: messages, ...errors })}>{error}</button></li>))}</ul> : null}
    {mode === 'uncertain' ? <RecoveryText label="Purchase commitment" text={recoveryText({ orderDate: date, draft: order.draft })} /> : null}
    <div className="button-row po-dialog-footer">
      <button className="secondary" type="button" disabled={mode === 'saving' || mode === 'uncertain'} onClick={cancel}>Keep draft</button>
      <button className="primary" type="button" disabled={mode === 'saving' || mode === 'blocked'} onClick={() => void confirm()}>{mode === 'saving' ? 'Recording…' : mode === 'uncertain' ? 'Check and retry commitment' : 'Confirm order'}</button>
    </div>
  </SupplierDialog>;
}
