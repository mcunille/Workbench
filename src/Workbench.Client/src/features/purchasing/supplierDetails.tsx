import type { SupplierContent } from '../../api/suppliers';
import { supplierFields } from './supplierSnapshot';
export function SupplierDetails({ heading, supplier, archived }: { heading: string; supplier: SupplierContent; archived?: boolean }) {
  return <section className="po-comparison-content"><h3>{heading}</h3>{archived ? <p>Archived supplier</p> : null}<dl className="po-comparison-details">
    {supplierFields.map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{supplier[key] || 'Not set'}</dd></div>)}
  </dl></section>;
}
