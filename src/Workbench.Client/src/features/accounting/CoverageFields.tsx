import { AccountingField, FieldError, type FieldErrors } from './AccountingField';
import { fieldId } from './accountingValidation';
import type { Account, Coverage } from '../../api/accounting';
const referenceFields = {
  label: ['Class and expected activity', 'Name the type of transaction and expected activity, such as supplier payments, bank fees, or transfers.'],
  sourceReference: ['Proposed source reference', 'Identify the record that would support these transactions, such as a purchase order or bank statement.'],
  policyReference: ['Policy reference', 'Describe or reference the accounting policy you plan to apply to this type of transaction.'],
  reconciliationReference: ['Reconciliation reference', 'Explain how you would check these transactions against a statement or another independent record.'],
  prerequisiteReference: ['Unresolved prerequisite', 'Describe what is still needed before these transactions can be accounted for, such as a missing workflow or policy decision.'],
};
export function CoverageFields({ accounts, value, change, errors = {} }: { accounts: Account[]; value: Coverage[]; change(value: Coverage[]): void; errors?: FieldErrors }) {
  function update(accountId: string, patch: Partial<Coverage>) {
    const current = value.find(item => item.accountId === accountId) ?? { accountId, included: true, attestedComplete: false, classes: [], exclusionRationale: null, evidenceKind: null, fromDate: null, toDate: null, evidenceReference: null, rationale: null };
    change([...value.filter(item => item.accountId !== accountId), { ...current, ...patch }]);
  }
  const funding = accounts.filter(account => !account.isArchived && ['Bank','Cash','CardLiability'].includes(account.purpose));
  return <><p>List the types of transactions each bank, cash, or card account needs to support, including recurring activity. This plan does not import statements or create opening balances. Workbench cannot yet account for these transactions.</p><FieldError field="coverage" errors={errors} />{!funding.length ? <p>Create a bank, cash, or card account to plan transaction coverage.</p> : funding.map(account => {
    const index = value.findIndex(item => item.accountId === account.id);
    const plan = value[index];
    const prefix = index >= 0 ? `coverage[${index}]` : `unplanned.${account.id}`;
    return <fieldset key={account.id} className="accounting-coverage"><legend>{account.code} — {account.name}</legend>
      <AccountingField field={`${prefix}.included`} label="Accounting perimeter" errors={errors} help="Include accounts you plan to use for this business. Exclude an account only with a recorded reason.">{attributes => <select {...attributes} value={!plan ? '' : plan.included ? 'include' : 'exclude'} onChange={e => update(account.id, { included: e.target.value === 'include' })}><option value="" disabled>Choose inclusion</option><option value="include">Include this account</option><option value="exclude">Exclude with rationale</option></select>}</AccountingField>
      {plan?.included === false ? <AccountingField field={`${prefix}.exclusionRationale`} label="Exclusion rationale" errors={errors}>{attributes => <textarea {...attributes} maxLength={2000} value={plan.exclusionRationale ?? ''} onChange={e => update(account.id, { exclusionRationale: e.target.value })} />}</AccountingField> : plan ? <>
        <AccountingField field={`${prefix}.evidenceKind`} label="Evidence basis" errors={errors} help="Use a representative statement to identify activity. Choose no prior activity for a new account with no transactions; still list expected activity. This declaration does not prove zero opening balances.">{attributes => <select {...attributes} value={plan.evidenceKind ?? ''} onChange={e => update(account.id, { evidenceKind: e.target.value || null, fromDate: null, toDate: null })}><option value="">Choose evidence basis</option><option value="Statement">Representative statement</option><option value="NoPriorActivity">No prior activity</option></select>}</AccountingField>
        <div className="accounting-grid">{plan.evidenceKind === 'Statement' ? <AccountingField field={`${prefix}.fromDate`} label="Statement from" errors={errors}>{attributes => <input {...attributes} type="date" value={plan.fromDate ?? ''} onChange={e => update(account.id, { fromDate: e.target.value || null })} />}</AccountingField> : null}
          <AccountingField field={`${prefix}.toDate`} label={plan.evidenceKind === 'NoPriorActivity' ? 'As-of date' : 'Statement to'} errors={errors}>{attributes => <input {...attributes} type="date" value={plan.toDate ?? ''} onChange={e => update(account.id, { toDate: e.target.value || null })} />}</AccountingField>
        </div>
        <AccountingField field={`${prefix}.evidenceReference`} label="Evidence description or reference" errors={errors}>{attributes => <textarea {...attributes} maxLength={500} value={plan.evidenceReference ?? ''} onChange={e => update(account.id, { evidenceReference: e.target.value })} />}</AccountingField>
        <AccountingField field={`${prefix}.rationale`} label="Rationale and recurring activity" errors={errors}>{attributes => <textarea {...attributes} maxLength={2000} value={plan.rationale ?? ''} onChange={e => update(account.id, { rationale: e.target.value })} />}</AccountingField>
        <div id={fieldId(`${prefix}.classes`)} tabIndex={-1} role="group" aria-label="Transaction classes"><FieldError field={`${prefix}.classes`} errors={errors} />
          {plan.classes.map((item, classIndex) => <fieldset key={classIndex}><legend>Transaction class {classIndex+1} · Unsupported</legend><div className="accounting-grid">{(Object.keys(referenceFields) as (keyof typeof referenceFields)[]).map(key => <AccountingField key={key} field={`${prefix}.classes[${classIndex}].${key}`} label={referenceFields[key][0]} help={referenceFields[key][1]} errors={errors}>{attributes => <input {...attributes} maxLength={key === 'label' ? 160 : 500} value={item[key] ?? ''} onChange={e => update(account.id, { classes: plan.classes.map((row, i) => i === classIndex ? { ...row, [key]: e.target.value } : row) })} />}</AccountingField>)}</div><button type="button" className="quiet" onClick={() => update(account.id, { classes: plan.classes.filter((_, i) => i !== classIndex), attestedComplete: false })}>Remove class {classIndex+1}</button></fieldset>)}
          <button type="button" className="secondary" onClick={() => update(account.id, { classes: [...plan.classes, { label: '', sourceReference: null, policyReference: null, reconciliationReference: null, prerequisiteReference: null }], attestedComplete: false })}>Add transaction class</button>
        </div>
        <label className="accounting-check"><input type="checkbox" checked={plan.attestedComplete} onChange={e => update(account.id, { attestedComplete: e.target.checked })} /> I have inventoried all statement and expected recurring activity.</label>
      </> : null}
    </fieldset>;
  })}</>;
}
