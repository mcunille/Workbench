import { ConfigurationSummary } from './ConfigurationSummary';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api/auth';
import { AccountingError, getAccountingCatalog, getAccountingSetup, getAccounts, saveAccountingSetup, type Account, type Catalog, type Configuration, type Setup } from '../../api/accounting';
import { PolicyFields } from './PolicyFields';
import { CoverageFields } from './CoverageFields';
import { Accounts } from './Accounts';
import { eligibleForMapping, label } from './accountingRules';
import './accounting.css';
interface Props { canManage: boolean; onAuthLost(): void; onDirtyChange(dirty: boolean, uncertain: boolean): void; }
export function AccountingSetup({ canManage, onAuthLost, onDirtyChange }: Props) {
  const [catalog, setCatalog] = useState<Catalog>(); const [saved, setSaved] = useState<Setup>(); const [draft, setDraft] = useState<Configuration>();
  const [accounts, setAccounts] = useState<Account[]>([]); const [tab, setTab] = useState('Policies'); const [accountDirty, setAccountDirty] = useState(false); const [accountUncertain, setAccountUncertain] = useState(false);
  const accountChanged = useCallback((dirty: boolean, uncertain: boolean) => { setAccountDirty(dirty); setAccountUncertain(uncertain); }, []);
  const [message, setMessage] = useState(''); const [errors, setErrors] = useState<Record<string, string[]>>({}); const [busy, setBusy] = useState(false); const [denied, setDenied] = useState(false);
  const [conflict, setConflict] = useState<Setup>(); const [uncertain, setUncertain] = useState(false);
  const pending = useRef<{ requestId: string; expectedVersion: string; configuration: Configuration } | undefined>(undefined);
  const dirty = !!draft && JSON.stringify(draft) !== JSON.stringify(saved?.configuration);
  useEffect(() => { onDirtyChange(dirty || accountDirty || uncertain, uncertain || accountUncertain); }, [dirty, accountDirty, uncertain, accountUncertain, onDirtyChange]);
  useEffect(() => () => onDirtyChange(false, false), [onDirtyChange]);
  const fail = useCallback((error: unknown) => {
    if (error instanceof ApiError && [401,403].includes(error.status)) { setDraft(undefined); setSaved(undefined); setAccounts([]); setConflict(undefined); setErrors({}); setMessage(''); setDenied(true); pending.current = undefined; setUncertain(false); setAccountDirty(false); onAuthLost(); return; }
    if (error instanceof AccountingError) { setErrors(error.errors); setMessage(error.detail); }
    else setMessage('Accounting could not be loaded or saved. Try again.');
  }, [onAuthLost]);
  async function readAllAccounts() {
    const all: Account[] = []; let cursor: string | null | undefined;
    do { const page = await getAccounts(cursor ?? undefined); all.push(...page.items); cursor = page.nextCursor; } while (cursor);
    return all;
  }
  const refreshAccounts = useCallback(async () => { setAccounts(await readAllAccounts()); }, []);
  useEffect(() => {
    let live = true;
    void Promise.all([getAccountingCatalog(), getAccountingSetup()]).then(([nextCatalog, nextSetup]) => { if (live) { setCatalog(nextCatalog); setSaved(nextSetup); setDraft(nextSetup.configuration); } }, error => { if (live) fail(error); });
    void readAllAccounts().then(next => { if (live) setAccounts(next); }, error => { if (live) fail(error); });
    return () => { live = false; pending.current = undefined; };
  }, [fail, refreshAccounts]);
  async function save() {
    if (!draft || !saved || busy) return;
    setBusy(true); setMessage(''); setErrors({});
    pending.current ??= { requestId: crypto.randomUUID(), expectedVersion: saved.version, configuration: structuredClone(draft) };
    try {
      await saveAccountingSetup(pending.current);
      const next = await getAccountingSetup(); pending.current = undefined; setUncertain(false); setSaved(next); setDraft(next.configuration); setConflict(undefined); setMessage('Accounting setup saved. Bookkeeping is not yet available.');
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        pending.current = undefined; setUncertain(false); setMessage('Setup changed elsewhere. Your draft is preserved. Load the current version to compare before saving again.');
        try { setConflict(await getAccountingSetup()); } catch (readError) { fail(readError); }
      } else { if (!(error instanceof ApiError) || error.status >= 500) setUncertain(true); else pending.current = undefined; fail(error); }
    } finally { setBusy(false); }
  }
  if (denied) return <><h1>Access denied</h1><p>Your accounting access has changed. Private drafts have been cleared.</p></>;
  if (!catalog || !saved || !draft) return <><h1>Accounting setup</h1><p role={message ? 'alert' : 'status'}>{message || 'Loading accounting setup…'}</p>{message ? <button type="button" className="secondary" onClick={() => window.location.reload()}>Reload accounting setup</button> : null}</>;
  return <div className="accounting-workspace"><h1>Accounting setup</h1><p className="lede">Save your business policies, accounts, and coverage plan. Bookkeeping is not yet available.</p>
    <details className="accounting-readiness">
      <summary>{saved.setupComplete ? 'Saved setup is complete' : `Saved setup: ${saved.missingItems.length} unanswered ${saved.missingItems.length === 1 ? 'item' : 'items'}`}</summary>
      <div className="accounting-status-details">
        {saved.missingItems.length ? <section aria-label="Unanswered setup items"><h2>Unanswered items</h2><ul>{saved.missingItems.map(item => <li key={item}>{label(item)}</li>)}</ul></section> : null}
        <section aria-label="Bookkeeping availability"><h2>Why bookkeeping is unavailable</h2><ul>{saved.blockers.map(item => <li key={item}>{item}</li>)}</ul></section>
      </div>
    </details>
    <div className="accounting-sections" role="group" aria-label="Setup sections">{['Policies','Accounts and mappings','Transaction coverage'].map(section => <button type="button" className="secondary" aria-pressed={tab === section} key={section} onClick={() => setTab(section)}>{section}</button>)}</div>
    {message ? <p role="alert">{message}</p> : null}{Object.entries(errors).map(([field, messages]) => <p role="alert" key={field}>{label(field)}: {messages.join(' ')}</p>)}
    {conflict ? <section aria-label="Compare concurrent changes"><h2>Resolve concurrent changes</h2><p>Your unsaved draft remains below. Compare it with the current saved configuration, then explicitly choose which version to continue with.</p><details><summary>Current saved configuration</summary><ConfigurationSummary value={conflict.configuration} accounts={accounts} /></details><div className="button-row"><button type="button" className="secondary" onClick={() => { setSaved(conflict); setConflict(undefined); setMessage('Current version loaded. Your draft is retained for reconciliation; review it before saving.'); }}>Keep my draft against this version</button><button type="button" className="secondary" onClick={() => { setSaved(conflict); setDraft(conflict.configuration); setConflict(undefined); setMessage('Current saved configuration loaded.'); }}>Discard my draft and use saved version</button></div></section> : null}
    <fieldset disabled={!canManage || busy || uncertain} className="accounting-section">
      <div hidden={tab !== 'Policies'}><h2>Policies</h2><p>Accrual foundation · Monthly accounting periods. Jurisdiction does not select tax rules. Retention remains a proposal until enforcement is available.</p><PolicyFields value={draft.policies} catalog={catalog} change={policies => setDraft({ ...draft, policies })} /></div>
      <div hidden={tab !== 'Accounts and mappings'}><a className="accounting-mappings-link" href="#accounting-mappings">Go to mappings</a><div className="accounting-accounts-layout"><Accounts catalog={catalog} changed={refreshAccounts} fail={fail} canManage={canManage} onDirtyChange={accountChanged} /><section aria-labelledby="accounting-mappings"><h2 id="accounting-mappings" tabIndex={-1}>Mappings</h2><p>Select accounts by purpose. Classification mappings are proposals, not active posting rules.</p><div className="accounting-grid">{catalog.mappingSlots.map(slot => <label key={slot}>{label(slot)}<select value={draft.mappings.find(item => item.slot === slot)?.accountId ?? ''} onChange={e => setDraft({ ...draft, mappings: [...draft.mappings.filter(item => item.slot !== slot), ...(e.target.value ? [{ slot, accountId: e.target.value }] : [])] })}><option value="">Not assigned</option>{accounts.filter(account => eligibleForMapping(slot, account)).map(account => <option key={account.id} value={account.id}>{account.code} — {account.name}</option>)}</select></label>)}</div></section></div></div>
      <div hidden={tab !== 'Transaction coverage'}><h2>Transaction coverage</h2><CoverageFields accounts={accounts} value={draft.coverage} change={coverage => setDraft({ ...draft, coverage })} /></div>
    </fieldset>
    {canManage ? <div className="button-row accounting-save"><button type="button" className="primary" disabled={busy || !!conflict || accountDirty} onClick={() => void save()}>{busy ? 'Saving…' : uncertain ? 'Retry the same save' : 'Save setup'}</button><span>{dirty ? 'Unsaved configuration' : 'Configuration matches saved version'}</span></div> : <p>You can view this configuration but cannot change it.</p>}
  </div>;
}
