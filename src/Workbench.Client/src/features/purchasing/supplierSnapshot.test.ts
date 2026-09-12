import { copySupplier, supplierSnapshot } from './supplierSnapshot';
import type { DraftContent } from '../../api/purchaseOrders';
const draft: DraftContent = { title: 'Order', supplierName: 'Old', supplierId: 'old', supplierContactName: 'Previous contact', supplierEmail: 'old@example.test', supplierPhone: '+44 old', supplierWebsite: 'https://old.example.test', supplierPostalAddress: 'Previous address', supplierOrderReference: 'EXT-1', platform: 'Gem Rock Auctions', currency: 'USD', notes: 'Order note', sourceLinks: ['https://example.test/listing'], entries: [] };
const supplier = { id: 'new', supplier: { name: 'New', contactName: 'New contact', email: 'new@example.test', phone: '+44 new', website: 'https://new.example.test', postalAddress: 'New line one\nNew line two' }, isArchived: false, version: 's1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
it('copies all directory contact fields and preserves every independent purchase field', () => {
  // GIVEN reviewed directory details and an existing purchase snapshot.
  const before = structuredClone(draft);
  // WHEN selecting or refreshing the supplier snapshot.
  const copied = copySupplier(draft, supplier);
  // THEN all contact details are copied, while platform, external reference and purchase content remain independent.
  expect(supplierSnapshot(copied)).toEqual(supplier.supplier);
  expect(copied.supplierId).toBe('new');
  expect(copied).toMatchObject({ title: draft.title, platform: 'Gem Rock Auctions', supplierOrderReference: 'EXT-1', currency: 'USD', notes: draft.notes, sourceLinks: draft.sourceLinks, entries: draft.entries });
  expect(draft).toEqual(before);
});
it('clears obsolete contact fields when a reviewed supplier has no optional details', () => {
  // GIVEN an old contact snapshot and an intentionally sparse supplier.
  const sparse = { ...supplier, supplier: { name: 'Sparse', contactName: null, email: null, phone: null, website: null, postalAddress: null } };
  // WHEN explicitly replacing the snapshot THEN old supplier contact details cannot leak into the new identity.
  expect(supplierSnapshot(copySupplier(draft, sparse))).toEqual(sparse.supplier);
});
