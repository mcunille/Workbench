import type { Account, Configuration } from '../../api/accounting';
import { label } from './accountingRules';
const policyLabels: Record<string, string> = {
  country: 'Country', region: 'State or region', currency: 'Functional currency', scale: 'Posting decimal places',
  fiscalStartMonth: 'Fiscal start month', startApproach: 'Starting approach', plannedStartDate: 'Planned start date',
  retentionYears: 'Proposed retention years', retentionRationale: 'Retention rationale', frameworkNotes: 'Framework and tax policy notes',
};
export function ConfigurationSummary({ value, accounts }: { value: Configuration; accounts: Account[] }) {
  function accountName(id: string) {
    const account = accounts.find(item => item.id === id);
    return account ? `${account.code} — ${account.name}` : 'Account unavailable in the current list';
  }
  return <div className="accounting-comparison">
    <h3>Saved policies</h3>
    <dl>{Object.entries(value.policies).map(([key, item]) => <div key={key}><dt>{policyLabels[key] ?? label(key)}</dt><dd>{item === null || item === '' ? 'Not supplied' : label(String(item))}</dd></div>)}</dl>
    <h3>Saved mappings</h3>
    {value.mappings.length ? <dl>{value.mappings.map(item => <div key={item.slot}><dt>{label(item.slot)}</dt><dd>{accountName(item.accountId)}</dd></div>)}</dl> : <p>No mappings assigned.</p>}
    <h3>Saved transaction coverage</h3>
    {value.coverage.length ? value.coverage.map(item => <section key={item.accountId}><h4>{accountName(item.accountId)}</h4>{item.included ? <>
      <p>{label(item.evidenceKind ?? 'Evidence basis not supplied')} · {item.fromDate ? `${item.fromDate} to ` : ''}{item.toDate ?? 'Date not supplied'}</p>
      <p>{item.evidenceReference || 'Evidence reference not supplied'}</p><p>{item.rationale || 'Rationale not supplied'}</p>
      <p>{item.attestedComplete ? 'Inventory attested complete' : 'Inventory not attested complete'}</p>
      <ul>{item.classes.map((entry, index) => <li key={index}><strong>{entry.label}</strong><dl>{(['sourceReference','policyReference','reconciliationReference','prerequisiteReference'] as const).map(key => <div key={key}><dt>{label(key)}</dt><dd>{entry[key] || 'Not supplied'}</dd></div>)}</dl></li>)}</ul>
    </> : <p>Excluded: {item.exclusionRationale || 'Rationale not supplied'}</p>}</section>) : <p>No coverage inventory supplied.</p>}
  </div>;
}
