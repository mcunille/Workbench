import { render, screen } from '@testing-library/react';
import { DraftComparison } from './DraftComparison';
it('renders all draft content as text and exposes only safe opener-isolated source links', () => {
  // GIVEN comparison includes local unsafe links, markup-like text, and an explicit zero price.
  render(<DraftComparison heading="Your changes" draft={{ title: '<script>example</script>', supplierName: 'Supplier', supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: 'Line one\nLine two', sourceLinks: ['javascript:alert(1)', 'https://example.test/source', 'https://name:secret@example.test/'], entries: [{ quantity: null, unitOfMeasure: 'carat', unitPrice: null, pricingUnit: null, pricePerQuantity: null, pricingQuantity: null, supplierSku: null, itemType: null, id: 'entry', description: 'Description', notes: 'Entry note', sourceLink: 'https://example.test/item', indicativePrice: '0.0000' }] }} />);
  // WHEN comparing THEN every value is readable and untrusted protocols or credentials cannot become links.
  expect(screen.getByText('<script>example</script>')).toBeVisible();
  expect(screen.getByText('0.00')).toBeVisible();
  // AND a selected unit remains visible even when its quantity is unknown.
  expect(screen.getByText('Quantity not set · Carat')).toBeVisible();
  expect(screen.getByText('Entry note')).toBeVisible();
  expect(screen.getAllByRole('link')).toHaveLength(2);
  for (const link of screen.getAllByRole('link')) { expect(link).toHaveAttribute('rel', 'noopener noreferrer'); expect(link).toHaveAttribute('target', '_blank'); }
});
