import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import type { DraftContent } from '../../api/purchaseOrders';
import { DraftCharges } from './DraftCharges';

const saved: DraftContent = { title: null, supplierName: 'Supplier', supplierId: null, supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: null, currency: 'USD', notes: null, sourceLinks: [], entries: [], orderDiscount: null, charges: [{ id: 'freight', category: 'shipping', label: 'Freight', amount: '15.00', payeeKind: 'thirdParty', payeeName: 'Carrier', amountStatus: 'confirmed', reference: null, notes: null }] };
function Harness({ errors = {} }: { errors?: Record<string, string[]> }) {
  const [draft, setDraft] = useState(saved);
  return <DraftCharges draft={draft} baseline={saved} disabled={false} amountDisabled={false} errors={errors} change={charges => setDraft({ ...draft, charges })} />;
}

it('summarizes saved charges and reveals their editable fields on demand', () => {
  // GIVEN a saved source-confirmed charge paid to a carrier.
  render(<Harness />);
  const summary = screen.getByLabelText('Edit charge 1: Freight');
  expect(summary).toHaveTextContent('Carrier');
  expect(summary).toHaveTextContent('Confirmed from source');
  expect(summary).toHaveTextContent('USD 15.00');
  expect(screen.getByLabelText('Charge amount 1')).not.toBeVisible();
  // WHEN expanded THEN its original amount is editable; collapsing preserves edits.
  fireEvent.click(summary);
  expect(screen.getByLabelText('Charge amount 1')).toBeVisible();
  fireEvent.change(screen.getByLabelText('Charge label 1'), { target: { value: 'Express freight' } });
  fireEvent.click(summary);
  expect(summary).toHaveTextContent('Express freight');
  expect(screen.getByLabelText('Charge label 1')).toHaveValue('Express freight');
});

it('opens and focuses added and restored charges without changing their identity', () => {
  // GIVEN a saved charge that the owner opens and removes.
  render(<Harness />);
  fireEvent.click(screen.getByLabelText('Edit charge 1: Freight'));
  fireEvent.click(screen.getByRole('button', { name: 'Remove charge 1' }));
  // WHEN undoing THEN the original charge returns expanded with focus for editing.
  fireEvent.click(screen.getByRole('button', { name: 'Undo charge removal' }));
  expect(screen.getByLabelText('Charge label 1')).toBeVisible();
  expect(screen.getByLabelText('Charge label 1')).toHaveFocus();
  expect(screen.getByLabelText('Charge amount 1')).toHaveValue('15.00');
  // WHEN adding another charge THEN its label is immediately ready for entry.
  fireEvent.click(screen.getByRole('button', { name: 'Add charge' }));
  expect(screen.getByLabelText('Charge label 2')).toBeVisible();
  expect(screen.getByLabelText('Charge label 2')).toHaveFocus();
});

it('reveals a collapsed charge when validation reports a problem', () => {
  // GIVEN a collapsed saved charge.
  const { rerender } = render(<Harness />);
  expect(screen.getByLabelText('Payee name 1')).not.toBeVisible();
  // WHEN validation identifies its payee THEN the field and explanation are visible.
  rerender(<Harness errors={{ 'draft.charges[0].payeeName': ['Check the payee.'] }} />);
  expect(screen.getByLabelText('Payee name 1')).toBeVisible();
  expect(screen.getByText('Check the payee.')).toBeVisible();
  // WHEN the owner starts correcting it and validation clears THEN editing stays open and focused.
  screen.getByLabelText('Payee name 1').focus();
  fireEvent.change(screen.getByLabelText('Payee name 1'), { target: { value: 'Updated carrier' } });
  rerender(<Harness />);
  expect(screen.getByLabelText('Payee name 1')).toBeVisible();
  expect(screen.getByLabelText('Payee name 1')).toHaveFocus();
});

it('keeps an unknown estimated amount explicit in the saved summary', () => {
  // GIVEN a saved charge whose amount is not yet known.
  const draft = { ...saved, charges: [{ ...saved.charges[0], amount: null, amountStatus: 'estimated' }] };
  render(<DraftCharges draft={draft} baseline={draft} disabled={false} amountDisabled={false} errors={{}} change={() => {}} />);
  // THEN the collapsed summary retains unknown status rather than implying zero.
  const summary = screen.getByLabelText('Edit charge 1: Freight');
  expect(summary).toHaveTextContent('Unknown');
  expect(summary).toHaveTextContent('Estimated');
});
