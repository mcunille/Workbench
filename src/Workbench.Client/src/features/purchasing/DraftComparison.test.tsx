import { render, screen } from '@testing-library/react';
import { DraftComparison } from './DraftComparison';
it('renders all draft content as text and exposes only safe opener-isolated source links', () => {
  // GIVEN comparison includes local unsafe links, markup-like text, and an explicit zero price.
  render(<DraftComparison heading="Your changes" draft={{ orderDiscount: null, charges: [], title: '<script>example</script>', supplierName: 'Supplier', supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: 'Line one\nLine two', sourceLinks: ['javascript:alert(1)', 'https://example.test/source', 'https://name:secret@example.test/'], entries: [{ discount: null, quantity: null, unitOfMeasure: 'carat', priceMode: 'perUnit', price: null, legacyPricing: null, supplierSku: null, itemType: null, id: 'entry', description: 'Description', notes: 'Entry note', sourceLink: 'https://example.test/item', indicativePrice: '0.0000' }] }} />);
  // WHEN comparing THEN every value is readable and untrusted protocols or credentials cannot become links.
  expect(screen.getByText('<script>example</script>')).toBeVisible();
  expect(screen.getByText('0.00')).toBeVisible();
  // AND a selected unit remains visible even when its quantity is unknown.
  expect(screen.getByText('Quantity not set · Carat')).toBeVisible();
  expect(screen.getByText('Entry note')).toBeVisible();
  expect(screen.getAllByRole('link')).toHaveLength(2);
  for (const link of screen.getAllByRole('link')) { expect(link).toHaveAttribute('rel', 'noopener noreferrer'); expect(link).toHaveAttribute('target', '_blank'); }
});

it('presents an absent custom title as optional metadata alongside supplier context', () => {
  // GIVEN a supplier-only purchase without a custom title.
  const draft = { title: null, supplierName: 'Gem supplier', supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: null, notes: null, sourceLinks: [], entries: [], orderDiscount: null, charges: [] };
  // WHEN comparing contents THEN supplier context remains visible without an Untitled heading.
  render(<DraftComparison heading="Current saved" draft={draft} />);
  expect(screen.getByText('Supplier').nextElementSibling).toHaveTextContent('Gem supplier');
  expect(screen.getByText('Custom title (optional)').nextElementSibling).toHaveTextContent('None');
  expect(screen.queryByText(/Untitled/)).not.toBeInTheDocument();
});
