import type { SupplierContent } from '../../api/suppliers';
import { supplierFields } from './supplierSnapshot';
export function SupplierDetails({ heading, supplier, archived, includeProfiles = false, fields = supplierFields.map(([key]) => key), populatedOnly = false, emptyLabel = 'Not set' }: { heading: string; supplier: SupplierContent; archived?: boolean; includeProfiles?: boolean; fields?: readonly (keyof SupplierContent)[]; populatedOnly?: boolean; emptyLabel?: string }) {
  return <section className="po-comparison-content"><h3>{heading}</h3>{archived ? <p>Archived supplier</p> : null}<dl className="po-comparison-details">
    {supplierFields.filter(([key]) => fields.includes(key) && (!populatedOnly || supplier[key])).map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{supplier[key] || emptyLabel}</dd></div>)}
    {includeProfiles ? supplier.socialProfiles?.length ? supplier.socialProfiles.map((profile, index) => <div key={`social-${index}`}><dt>{profile.label}</dt><dd>{profile.handle}</dd></div>) : !populatedOnly ? <div><dt>Social handles</dt><dd>{emptyLabel}</dd></div> : null : null}
  </dl></section>;
}
