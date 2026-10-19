import { useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getGem, setTenantGemArchived, GemReferenceApiError, type GemReferenceDetailResponse, type GemReferenceTenantProblemResponse } from '../../api/gemReference';
import { GemConfirmationDialog } from './GemConfirmationDialog';

export function GemTenantActions({ entry, onChange, onAuthLost }: { entry: GemReferenceDetailResponse; onChange: (entry: GemReferenceDetailResponse) => void; onAuthLost: () => void }) {
  const [confirm, setConfirm] = useState(false);
  const [pending, setPending] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const [message, setMessage] = useState('');
  const busy = useRef(false);
  const archived = !!entry.isArchived;
  function auth(error: unknown) { if ((error instanceof GemReferenceApiError || error instanceof ApiError) && (error.status === 401 || error.status === 403)) { onAuthLost(); return true; } return false; }
  async function change() {
    if (busy.current || !entry.effectiveVersion) return;
    busy.current = true; setPending(true); setConfirm(false); setMessage('');
    try { onChange(await setTenantGemArchived(entry.id, !archived, entry.effectiveVersion)); setMessage(archived ? 'Tenant entry restored.' : 'Tenant entry archived.'); }
    catch (error) {
      if (auth(error)) return;
      if (error instanceof GemReferenceApiError && error.status < 500) {
        const problem = error.problem as Partial<GemReferenceTenantProblemResponse> | undefined;
        if (problem?.code === 'stale_entry' && problem.current?.id === entry.id && problem.current.origin === 'tenant') { onChange(problem.current); setMessage('This entry changed. Review its current values before confirming again.'); }
        else setMessage(`The entry was not ${archived ? 'restored' : 'archived'}. ${Object.values(problem?.errors ?? {}).flat().join(' ') || 'Try again.'}`);
      } else { setUncertain(true); setMessage('Change not confirmed. Check the current entry before trying again.'); }
    } finally { busy.current = false; setPending(false); }
  }
  async function check() {
    if (busy.current) return;
    busy.current = true; setPending(true);
    try { onChange(await getGem(entry.id, 'tenant')); setUncertain(false); setMessage('Current saved entry loaded. Review it before another change.'); }
    catch (error) { if (!auth(error)) setMessage('We could not check the current entry. Try checking again.'); }
    finally { busy.current = false; setPending(false); }
  }
  return <div className="reference-tenant-actions">{message ? <p role="alert">{message}</p> : null}
    <button type="button" className="secondary" disabled={pending || uncertain || !entry.effectiveVersion} onClick={() => setConfirm(true)}>{archived ? 'Restore tenant entry' : 'Archive tenant entry'}</button>
    {uncertain ? <button type="button" disabled={pending} onClick={() => void check()}>Check current entry</button> : null}
    {confirm ? <GemConfirmationDialog title={archived ? 'Restore tenant entry?' : 'Archive tenant entry?'} cancel={() => setConfirm(false)}><p>{archived ? 'This returns the entry to ordinary searches if its effective identity is valid.' : 'This hides the entry from ordinary searches. Include archived tenant entries in the library to find and restore it.'}</p><div className="button-row"><button type="button" className="secondary" autoFocus onClick={() => setConfirm(false)}>Cancel</button><button type="button" onClick={() => void change()}>{archived ? 'Restore entry' : 'Archive entry'}</button></div></GemConfirmationDialog> : null}
  </div>;
}
