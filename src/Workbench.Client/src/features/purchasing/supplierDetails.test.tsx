import { render, screen } from '@testing-library/react';
import { SupplierDetails } from './supplierDetails';
import { emptySupplier } from './supplierSnapshot';
const supplier = { ...emptySupplier(), name: 'Supplier', socialProfiles: [{ label: 'Discord', handle: '@gems' }, { label: '<Forum>', handle: 'https://example.test/member' }] };

it('keeps order snapshot displays limited to contact fields', () => {
  // GIVEN directory handles that are not copied into order snapshots.
  // WHEN displaying contact snapshots THEN social fields cannot imply saved order content.
  render(<SupplierDetails heading="Order supplier" supplier={supplier} />);
  expect(screen.queryByText('Discord')).not.toBeInTheDocument();
  expect(screen.queryByText('@gems')).not.toBeInTheDocument();
});

it('compares arbitrary labels and handles as text without opening links', () => {
  // GIVEN user-defined labels and URL-like reference text.
  // WHEN comparing supplier records THEN each pair is visible as plain text.
  render(<SupplierDetails includeProfiles heading="Saved supplier" supplier={supplier} />);
  expect(screen.getByText('Discord')).toBeVisible();
  expect(screen.getByText('@gems')).toBeVisible();
  expect(screen.getByText('<Forum>')).toBeVisible();
  expect(screen.getByText('https://example.test/member')).toBeVisible();
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
});

it('makes removal of all social handles explicit in comparisons', () => {
  // GIVEN a supplier with no handles WHEN comparing THEN the empty state is explicit.
  render(<SupplierDetails includeProfiles heading="Your changes" supplier={{ ...supplier, socialProfiles: [] }} />);
  expect(screen.getByText('Social handles')).toBeVisible();
});
