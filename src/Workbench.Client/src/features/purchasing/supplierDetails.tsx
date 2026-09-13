import type { SupplierContent } from '../../api/suppliers';
import { supplierFields } from './supplierSnapshot';
export function SupplierDetails({ heading, supplier, archived, fields = supplierFields.map(([key]) => key), populatedOnly = false, emptyLabel = 'Not set' }: { heading: string; supplier: SupplierContent; archived?: boolean; fields?: readonly (keyof SupplierContent)[]; populatedOnly?: boolean; emptyLabel?: string }) {
  return <section className="po-comparison-content"><h3>{heading}</h3>{archived ? <p>Archived supplier</p> : null}<dl className="po-comparison-details">
    {supplierFields.filter(([key]) => fields.includes(key) && (!populatedOnly || supplier[key])).map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{supplier[key] || emptyLabel}</dd></div>)}
  </dl></section>;
}
