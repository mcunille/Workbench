import type { DraftContent } from '../../api/purchaseOrders';
import type { SupplierContent, Supplier } from '../../api/suppliers';
export const supplierFields = [
  ['name', 'Name', 200], ['contactName', 'Contact name', 200], ['email', 'Email', 254],
  ['phone', 'Phone', 100], ['website', 'Website', 2048], ['postalAddress', 'Postal address', 2000],
] as const;
export const emptySupplier = (): SupplierContent => ({ name: '', contactName: null, email: null, phone: null, website: null, postalAddress: null });
export function supplierSnapshot(draft: DraftContent): SupplierContent {
  return { name: draft.supplierName ?? '', contactName: draft.supplierContactName, email: draft.supplierEmail, phone: draft.supplierPhone, website: draft.supplierWebsite, postalAddress: draft.supplierPostalAddress };
}
export function copySupplier(draft: DraftContent, selected: Supplier): DraftContent {
  const source = selected.supplier;
  return { ...draft, supplierId: selected.id, supplierName: source.name, supplierContactName: source.contactName,
    supplierEmail: source.email, supplierPhone: source.phone, supplierWebsite: source.website, supplierPostalAddress: source.postalAddress };
}
