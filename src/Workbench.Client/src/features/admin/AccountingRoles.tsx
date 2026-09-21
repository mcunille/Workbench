import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { getAccountingRoles, getRoleAssignment, saveRoleAssignment, type AccountingRole, type RoleAssignment } from '../../api/accountingRoles';
export function AccountingRoles({ userId, email, close, onAuthLost, onRolesSaved, onDirtyChange }: { userId: string; email: string; close(): void; onAuthLost?(): void; onRolesSaved?(): void; onDirtyChange?(dirty: boolean, uncertain: boolean): void }) {
  const [roles, setRoles] = useState<AccountingRole[]>([]); const [saved, setSaved] = useState<RoleAssignment>(); const [selected, setSelected] = useState<string[]>([]);
  const [uncertain, setUncertain] = useState(false);
  const [message, setMessage] = useState(''); const [busy, setBusy] = useState(false); const [denied, setDenied] = useState(false); const [conflict, setConflict] = useState(false);
  const pending = useRef<{ requestId: string; expectedVersion: string; roleIds: string[] } | undefined>(undefined);
  const dirty = !!saved && JSON.stringify([...selected].sort()) !== JSON.stringify([...saved.roleIds].sort());
  useEffect(() => { onDirtyChange?.(dirty || uncertain || busy, uncertain || busy); }, [dirty, uncertain, busy, onDirtyChange]);
  useEffect(() => () => onDirtyChange?.(false, false), [onDirtyChange]);
  useEffect(() => { let live = true; void Promise.all([getAccountingRoles(), getRoleAssignment(userId)]).then(([catalog, assignment]) => { if (live) { setRoles(catalog); setSaved(assignment); setSelected(assignment.roleIds); } }, () => { if (live) setMessage('Accounting roles could not be loaded. Close this editor and try again.'); }); return () => { live = false; pending.current = undefined; }; }, [userId]);
  async function save() {
    if (!saved || busy) return; setBusy(true);
    pending.current ??= { requestId: crypto.randomUUID(), expectedVersion: saved.version, roleIds: selected };
    try { await saveRoleAssignment(userId, pending.current); const next = await getRoleAssignment(userId); pending.current = undefined; setUncertain(false); setSaved(next); setSelected(next.roleIds); setMessage('Accounting roles saved. Changes take effect on the next request.'); onRolesSaved?.(); }
    catch (error) { if (error instanceof ApiError && [401,403].includes(error.status)) { pending.current = undefined; setSaved(undefined); setSelected([]); setRoles([]); setDenied(true); setUncertain(false); onAuthLost?.(); } else if (error instanceof ApiError && error.status === 409) { pending.current = undefined; setUncertain(false); setConflict(true); setMessage('Role assignment changed. Your selection is preserved. Reload roles before reconciling.'); } else { if (error instanceof ApiError && error.status < 500) { pending.current = undefined; setUncertain(false); if (error.status === 404) { setSaved(undefined); setSelected([]); setRoles([]); setConflict(false); setMessage('This user is unavailable. Close this editor and choose an enabled user.'); } else setMessage('The assignment was rejected. Close and reload roles before trying again.'); } else { setUncertain(true); setMessage('Roles could not be saved. Retry the same assignment.'); } } }
    finally { setBusy(false); }
  }
  return <section className="accounting-roles" aria-label={`Accounting roles for ${email}`}><h3>Accounting roles for {email}</h3>{denied ? <p role="alert">Access denied. Private role selections have been cleared.</p> : !saved ? <p role="status">{message || 'Loading accounting roles…'}</p> : <>
    <p>Accounting administrator manages setup and future reconciliation, closing, and reports. Accounting reader can view and export future reports. Neither role grants user administration or payment authority.</p>
    <fieldset disabled={busy || uncertain || conflict}>{roles.map(role => <label className="accounting-check" key={role.id}><input type="checkbox" checked={selected.includes(role.id)} onChange={e => setSelected(previous => e.target.checked ? [...previous, role.id] : previous.filter(id => id !== role.id))} />{role.name}</label>)}</fieldset>
    <p>Proposed grants: {roles.filter(role => selected.includes(role.id) && !saved.roleIds.includes(role.id)).map(role => role.name).join(', ') || 'None'}. Proposed revocations: {roles.filter(role => !selected.includes(role.id) && saved.roleIds.includes(role.id)).map(role => role.name).join(', ') || 'None'}.</p>
    <details><summary>Resulting accounting permissions</summary><ul>{[...new Set(roles.filter(role => selected.includes(role.id)).flatMap(role => role.permissions))].map(permission => <li key={permission}>{permission.replace(/([a-z])([A-Z])/g, '$1 $2')}</li>)}</ul><p>Other assigned roles continue to apply.</p></details>
    <button type="button" className="primary" disabled={busy || conflict} onClick={() => void save()}>{busy ? 'Saving roles…' : 'Save accounting roles'}</button>
    {conflict ? <button type="button" className="secondary" onClick={() => { void getRoleAssignment(userId).then(next => { setSaved(next); setConflict(false); setMessage('Current roles loaded. Review proposed changes before saving.'); }).catch(error => { if (error instanceof ApiError && [401,403].includes(error.status)) { setSaved(undefined); setSelected([]); setRoles([]); pending.current = undefined; setUncertain(false); setConflict(false); setDenied(true); onAuthLost?.(); } else setMessage('Current roles could not be loaded. Try again.'); }); }}>Reload roles and retain selection</button> : null}
    {message ? <p role="status">{message}</p> : null}
  </>}<button type="button" className="secondary" disabled={busy || uncertain} onClick={close}>Close roles</button></section>;
}




