import { purchaseLabel } from './purchaseLabel';

it.each([
  [{ title: 'Special parcel', supplierName: 'Supplier', firstItemDescription: 'Sapphire' }, 'Draft', 'Special parcel'],
  [{ title: ' ', supplierName: 'Supplier', firstItemDescription: 'Sapphire' }, 'Draft', 'Supplier'],
  [{ title: null, supplierName: null, firstItemDescription: 'Sapphire' }, 'Draft', 'Sapphire'],
  [{ title: null, supplierName: null, entries: [{ description: ' ' }, { description: 'Ruby' }] }, 'Draft', 'Ruby'],
  [{ title: null, supplierName: null }, 'Draft', 'Empty draft'],
  [{ title: null, supplierName: null }, 'Ordered', 'Purchase order'],
] as const)('uses available purchase context: %s', (value, state, expected) => {
  // GIVEN available custom, supplier or item context, or a completely empty purchase.
  const purchase = value;
  // WHEN deriving a display label THEN context takes precedence over the neutral fallback.
  expect(purchaseLabel(purchase, state)).toBe(expected);
  // AND deriving a label never changes the saved title.
  expect(purchase.title).toBe(value.title);
});
