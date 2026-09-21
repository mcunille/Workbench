import { FloatingField } from '../../FloatingField';
import type { Policies, Catalog } from '../../api/accounting';
export function PolicyFields({ value, catalog, change }: { value: Policies; catalog: Catalog; change(value: Policies): void }) {
  function set(key: keyof Policies, next: string | number | null) { change({ ...value, [key]: next }); }
  const country = catalog.countries.find(item => item.code === value.country);
  return <div className="accounting-policy-groups">
    <fieldset className="accounting-policy-group"><legend>Location and currency</legend><div className="accounting-grid">
    <label>Country<select value={value.country ?? ''} onChange={e => change({ ...value, country: e.target.value || null, region: null })}><option value="">Choose country</option>{catalog.countries.map(item => <option key={item.code} value={item.code}>{item.name}</option>)}</select></label>
    <label>State or region<select value={value.region ?? ''} disabled={!country?.regions.length} onChange={e => set('region', e.target.value || null)}><option value="">{country?.regions.length ? 'Choose region' : 'Not applicable'}</option>{country?.regions.map(item => <option key={item.code} value={item.code}>{item.name}</option>)}</select></label>
    <label>Functional currency<select value={value.currency ?? ''} onChange={e => set('currency', e.target.value || null)}><option value="">Choose currency</option>{catalog.currencies.map(item => <option key={item.code} value={item.code}>{item.code} — {item.name}</option>)}</select></label>
    <label>Posting decimal places<select value={value.scale ?? ''} onChange={e => set('scale', e.target.value === '' ? null : Number(e.target.value))}><option value="">Confirm precision</option>{[0,1,2,3,4].map(scale => <option key={scale}>{scale}</option>)}</select></label>
    </div></fieldset>
    <fieldset className="accounting-policy-group"><legend>Accounting dates</legend><div className="accounting-grid">
    <label>Fiscal year starts<select value={value.fiscalStartMonth ?? ''} onChange={e => set('fiscalStartMonth', e.target.value ? Number(e.target.value) : null)}><option value="">Choose month</option>{Array.from({ length: 12 }, (_, i) => <option key={i} value={i+1}>{new Intl.DateTimeFormat('en', { month: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(2024,i,1)))}</option>)}</select></label>
    <label>Starting approach<select value={value.startApproach ?? ''} onChange={e => set('startApproach', e.target.value || null)}><option value="">Choose approach</option><option value="FromBeginning">Complete history from the beginning</option><option value="OpeningBalances">Reconciled opening balances</option></select></label>
    <label>Planned start date<input type="date" value={value.plannedStartDate ?? ''} onChange={e => set('plannedStartDate', e.target.value || null)} /></label>
    </div></fieldset>
    <fieldset className="accounting-policy-group"><legend>Retention and policy notes</legend><div className="accounting-grid">
    <FloatingField label="Proposed document retention (years)" htmlFor="retention-years"><input id="retention-years" type="number" min="1" step="1" placeholder=" " value={value.retentionYears ?? ''} onChange={e => set('retentionYears', e.target.value ? Number(e.target.value) : null)} /></FloatingField>
    <FloatingField label="Retention rationale or reference" htmlFor="retention-rationale"><textarea id="retention-rationale" placeholder=" " value={value.retentionRationale ?? ''} onChange={e => set('retentionRationale', e.target.value || null)} /></FloatingField>
    <FloatingField label="Framework and tax policy notes (optional)" htmlFor="framework-notes"><textarea id="framework-notes" placeholder=" " value={value.frameworkNotes ?? ''} onChange={e => set('frameworkNotes', e.target.value || null)} /></FloatingField>
    </div></fieldset>
  </div>;
}
