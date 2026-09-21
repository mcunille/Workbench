import { FloatingField } from '../../FloatingField';
import type { Account, Coverage } from '../../api/accounting';
export function CoverageFields({ accounts, value, change }: { accounts: Account[]; value: Coverage[]; change(value: Coverage[]): void }) {
  function update(accountId: string, patch: Partial<Coverage>) {
    const current = value.find(item => item.accountId === accountId) ?? { accountId, included: true, attestedComplete: false, classes: [], exclusionRationale: null, evidenceKind: null, fromDate: null, toDate: null, evidenceReference: null, rationale: null };
    change([...value.filter(item => item.accountId !== accountId), { ...current, ...patch }]);
  }
  const funding = accounts.filter(account => !account.isArchived && ['Bank','Cash','CardLiability'].includes(account.purpose));
  return <><p>Inventory every expected transaction class. All financial classes remain unsupported until their accounting workflows ship. This inventory does not establish opening balances.</p>{!funding.length ? <p>Create a bank, cash, or card account to plan transaction coverage.</p> : funding.map(account => {
    const plan = value.find(item => item.accountId === account.id);
    return <fieldset key={account.id} className="accounting-coverage"><legend>{account.code} — {account.name}</legend>
      <label>Accounting perimeter<select value={!plan ? '' : plan.included ? 'include' : 'exclude'} onChange={e => update(account.id, { included: e.target.value === 'include' })}><option value="" disabled>Choose inclusion</option><option value="include">Include this account</option><option value="exclude">Exclude with rationale</option></select></label>
      {plan?.included === false ? <FloatingField label="Exclusion rationale" htmlFor={`exclude-${account.id}`}><textarea id={`exclude-${account.id}`} placeholder=" " value={plan.exclusionRationale ?? ''} onChange={e => update(account.id, { exclusionRationale: e.target.value })} /></FloatingField> : plan ? <>
        <label>Evidence basis<select value={plan.evidenceKind ?? ''} onChange={e => update(account.id, { evidenceKind: e.target.value || null, fromDate: null, toDate: null })}><option value="">Choose evidence basis</option><option value="Statement">Representative statement</option><option value="NoPriorActivity">No prior activity</option></select></label>
        <div className="accounting-grid">{plan.evidenceKind === 'Statement' ? <label>Statement from<input type="date" value={plan.fromDate ?? ''} onChange={e => update(account.id, { fromDate: e.target.value || null })} /></label> : null}
        <label>{plan.evidenceKind === 'NoPriorActivity' ? 'As-of date' : 'Statement to'}<input type="date" value={plan.toDate ?? ''} onChange={e => update(account.id, { toDate: e.target.value || null })} /></label></div>
        <FloatingField label="Evidence description or reference" htmlFor={`evidence-${account.id}`}><textarea id={`evidence-${account.id}`} placeholder=" " value={plan.evidenceReference ?? ''} onChange={e => update(account.id, { evidenceReference: e.target.value })} /></FloatingField>
        <FloatingField label="Rationale and recurring activity" htmlFor={`rationale-${account.id}`}><textarea id={`rationale-${account.id}`} placeholder=" " value={plan.rationale ?? ''} onChange={e => update(account.id, { rationale: e.target.value })} /></FloatingField>
        {plan.classes.map((item, index) => <fieldset key={index}><legend>Transaction class {index+1} · Unsupported</legend><div className="accounting-grid">{(['label','sourceReference','policyReference','reconciliationReference','prerequisiteReference'] as const).map(key => <FloatingField key={key} label={{ label: 'Class and expected activity', sourceReference: 'Proposed source reference', policyReference: 'Policy reference', reconciliationReference: 'Reconciliation reference', prerequisiteReference: 'Unresolved prerequisite' }[key]} htmlFor={`${account.id}-${index}-${key}`}><input id={`${account.id}-${index}-${key}`} placeholder=" " value={item[key] ?? ''} onChange={e => update(account.id, { classes: plan.classes.map((row, i) => i === index ? { ...row, [key]: e.target.value } : row) })} /></FloatingField>)}</div><button type="button" className="quiet" onClick={() => update(account.id, { classes: plan.classes.filter((_, i) => i !== index), attestedComplete: false })}>Remove class {index+1}</button></fieldset>)}
        <button type="button" className="secondary" onClick={() => update(account.id, { classes: [...plan.classes, { label: '', sourceReference: null, policyReference: null, reconciliationReference: null, prerequisiteReference: null }], attestedComplete: false })}>Add transaction class</button>
        <label className="accounting-check"><input type="checkbox" checked={plan.attestedComplete} onChange={e => update(account.id, { attestedComplete: e.target.checked })} /> I have inventoried all statement and expected recurring activity.</label>
      </> : null}
    </fieldset>;
  })}</>;
}

