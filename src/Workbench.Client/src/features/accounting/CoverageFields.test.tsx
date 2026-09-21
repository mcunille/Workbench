import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { CoverageFields } from './CoverageFields';
import type { Coverage } from '../../api/accounting';
function Fixture() {
  const [value, setValue] = useState<Coverage[]>([]);
  return <><CoverageFields accounts={[{ id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'Bank', description: null, isArchived: false, version: 'v1' }]} value={value} change={setValue} /><output>{JSON.stringify(value)}</output></>;
}
it('explains coverage choices and reference fields without claiming financial support', () => {
  // GIVEN a bank account whose coverage has not been planned
  render(<Fixture />);
  // THEN the inclusion decision has plain-language help
  expect(screen.getByLabelText('Accounting perimeter')).toHaveAccessibleDescription(/include.*business.*exclude.*reason/i);
  // WHEN included THEN evidence guidance distinguishes a statement from no prior activity
  fireEvent.change(screen.getByLabelText('Accounting perimeter'), { target: { value: 'include' } });
  expect(screen.getByLabelText('Evidence basis')).toHaveAccessibleDescription(/no prior activity.*expected.*does not prove zero/i);
  fireEvent.click(screen.getByRole('button', { name: 'Add transaction class' }));
  expect(screen.getByLabelText('Class and expected activity')).toHaveAccessibleDescription(/fees.*transfers/i);
  expect(screen.getByLabelText('Reconciliation reference')).toHaveAccessibleDescription(/check.*statement/i);
  expect(screen.getByText(/does not import statements or create opening balances/i)).toBeVisible();
});
it('records no-prior-activity as a dated plan with explicitly unsupported future classes', () => {
  // GIVEN a new bank account without statement history
  render(<Fixture />);
  // WHEN planning coverage THEN a no-prior-activity declaration uses an as-of date
  fireEvent.change(screen.getByLabelText('Accounting perimeter'), { target: { value: 'include' } });
  fireEvent.change(screen.getByLabelText('Evidence basis'), { target: { value: 'NoPriorActivity' } });
  fireEvent.change(screen.getByLabelText('As-of date'), { target: { value: '2026-01-01' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add transaction class' }));
  fireEvent.change(screen.getByLabelText('Class and expected activity'), { target: { value: 'Bank fees' } });
  expect(screen.getByText('Transaction class 1 · Unsupported')).toBeVisible();
  expect(screen.getByRole('status')).toHaveTextContent('"toDate":"2026-01-01"');
  expect(screen.queryByLabelText('Supported')).not.toBeInTheDocument();
});
it('retains an explicit rationale for an excluded funding account', () => {
  // GIVEN a funding account outside the proposed perimeter
  render(<Fixture />);
  // WHEN excluded THEN its reason stays in the coverage plan
  fireEvent.change(screen.getByLabelText('Accounting perimeter'), { target: { value: 'exclude' } });
  fireEvent.change(screen.getByLabelText('Exclusion rationale'), { target: { value: 'Personal account outside this business' } });
  expect(screen.getByRole('status')).toHaveTextContent('"included":false');
  expect(screen.getByRole('status')).toHaveTextContent('Personal account outside this business');
});
it('keeps unplanned account controls uniquely labelled and applies server text limits', () => {
  // GIVEN two accounts that do not yet have coverage records
  const bank = { id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'Bank', description: null, isArchived: false, version: 'v1' };
  const { container, rerender } = render(<CoverageFields accounts={[bank, { ...bank, id: 'cash', code: '1010', name: 'Cash' }]} value={[]} change={vi.fn()} />);
  // THEN all label and help identifiers remain unique before either account is included
  const ids = Array.from(container.querySelectorAll('[id]'), element => element.id);
  expect(new Set(ids).size).toBe(ids.length);
  expect(screen.getAllByLabelText('Accounting perimeter')).toHaveLength(2);
  // WHEN a class is present THEN labels remain persistent and input limits match the API
  rerender(<CoverageFields accounts={[bank]} value={[{ accountId: 'bank', included: true, attestedComplete: false, classes: [{ label: '', sourceReference: null, policyReference: null, reconciliationReference: null, prerequisiteReference: null }], evidenceKind: 'Statement', fromDate: null, toDate: null, evidenceReference: null, rationale: null, exclusionRationale: null }]} change={vi.fn()} />);
  expect(screen.getByLabelText('Class and expected activity')).toHaveAttribute('maxlength', '160');
  expect(screen.getByLabelText('Evidence description or reference')).toHaveAttribute('maxlength', '500');
});
