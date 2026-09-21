import { render, screen } from '@testing-library/react';
import { SupplierDetails } from './supplierDetails';
import { emptySupplier } from './supplierSnapshot';

it('keeps order snapshot displays limited to contact fields', () => {
  // GIVEN directory profiles that are not copied into order snapshots.
  const supplier = { ...emptySupplier(), name: 'Supplier', instagram: 'https://instagram.com/example' };
  // WHEN displaying contact snapshots THEN profile fields cannot imply saved order content.
  render(<SupplierDetails heading="Order supplier" supplier={supplier} />);
  expect(screen.queryByText('Instagram')).not.toBeInTheDocument();
  expect(screen.queryByText('X')).not.toBeInTheDocument();
  expect(screen.queryByText('GemRockAuctions')).not.toBeInTheDocument();
});

it('includes profile values and removals when comparing supplier records', () => {
  // GIVEN a supplier comparison with a profile and two absent platforms.
  const supplier = { ...emptySupplier(), name: 'Supplier', instagram: 'https://instagram.com/example' };
  // WHEN explicitly comparing supplier records THEN platform values and absence are visible.
  render(<SupplierDetails includeProfiles heading="Saved supplier" supplier={supplier} />);
  expect(screen.getByText('Instagram')).toBeVisible();
  expect(screen.getByText('https://instagram.com/example')).toBeVisible();
  expect(screen.getByText('X')).toBeVisible();
  expect(screen.getByText('GemRockAuctions')).toBeVisible();
});
