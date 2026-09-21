export const fieldId = (field: string) => `accounting-${field}`;
const fieldNames: Record<string, string> = {
  country: 'Country', region: 'State or region', currency: 'Functional currency', scale: 'Posting decimal places',
  fiscalStartMonth: 'Fiscal year starts', startApproach: 'Starting approach', plannedStartDate: 'Planned start date',
  retentionYears: 'Proposed document retention (years)', retentionRationale: 'Retention rationale or reference', frameworkNotes: 'Framework and tax policy notes',
  mappings: 'Mappings', coverage: 'Transaction coverage', included: 'Accounting perimeter', exclusionRationale: 'Exclusion rationale',
  evidenceKind: 'Evidence basis', evidenceReference: 'Evidence description or reference', rationale: 'Rationale and recurring activity', classes: 'Transaction classes',
  label: 'Class and expected activity', sourceReference: 'Proposed source reference', policyReference: 'Policy reference', reconciliationReference: 'Reconciliation reference', prerequisiteReference: 'Unresolved prerequisite',
};
export function fieldName(field: string) { return fieldNames[field.split('.').at(-1)?.replace(/\[\d+\]/g, '') ?? ''] ?? field; }
