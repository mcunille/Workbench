import { fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { CoverageFields } from './CoverageFields';
import type { Coverage } from '../../api/accounting';
function Fixture() {
  const [value, setValue] = useState<Coverage[]>([]);
  return <><CoverageFields accounts={[{ id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'Bank', description: null, isArchived: false, version: 'v1' }]} value={value} change={setValue} /><output>{JSON.stringify(value)}</output></>;
}
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
