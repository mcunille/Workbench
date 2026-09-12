import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { ReferencePriceField } from './ReferencePriceField';
function Harness({ initial = null }: { initial?: string | null }) {
  const [value, setValue] = useState<string | null>(initial);
  return <ReferencePriceField id="price" index={1} value={value} onChange={setValue} disabled={false} />;
}
it('shifts digits into cents, deletes to unknown, and distinguishes explicit zero', () => {
  // GIVEN an unknown price.
  render(<Harness />); const field = screen.getByLabelText('Reference price 1');
  expect(field).toHaveValue('');
  // WHEN appending digits THEN they fill two decimal places from the right.
  for (const [typed, expected] of [['1','0.01'], ['0.012','0.12'], ['0.123','1.23'], ['1.234','12.34']]) {
    fireEvent.change(field, { target: { value: typed } }); expect(field).toHaveValue(expected);
  }
  // WHEN removing digits THEN the final deletion returns to unknown.
  for (const expected of ['1.23','0.12','0.01','']) {
    fireEvent.keyDown(field, { key: 'Backspace' }); expect(field).toHaveValue(expected);
  }
  fireEvent.change(field, { target: { value: '0' } }); expect(field).toHaveValue('0.00');
});
it('preserves existing extra precision and exact large amounts', () => {
  // GIVEN a persisted price with meaningful fourth-decimal precision.
  render(<Harness initial="999999999999999.1234" />);
  const field = screen.getByLabelText('Reference price 1');
  const toggle = screen.getByRole('checkbox');
  // THEN precision mode is automatic and cannot silently round the amount.
  expect(toggle).toBeChecked(); expect(toggle).toBeDisabled();
  expect(field).toHaveValue('999999999999999.1234');
  // WHEN extra digits are removed THEN ordinary two-decimal entry becomes available.
  fireEvent.change(field, { target: { value: '12.34' } });
  expect(toggle).not.toBeChecked(); expect(toggle).toBeEnabled();
});
it('supports deliberate decimal entry and preserves pasted decimals and invalid signs', () => {
  // GIVEN a user choosing extra precision for a new price.
  render(<Harness />); const field = screen.getByLabelText('Reference price 1');
  fireEvent.click(screen.getByRole('checkbox'));
  // WHEN typing a decimal THEN it is kept exactly.
  fireEvent.change(field, { target: { value: '0.0123' } }); expect(field).toHaveValue('0.0123');
  fireEvent.change(field, { target: { value: '' } });
  fireEvent.click(screen.getByRole('checkbox'));
  // WHEN pasting THEN decimal values and invalid negative signs are never silently reinterpreted.
  fireEvent.paste(field, { clipboardData: { getData: () => '125.5012' } });
  expect(field).toHaveValue('125.5012'); expect(screen.getByRole('checkbox')).toBeChecked();
  fireEvent.paste(field, { clipboardData: { getData: () => '-12.34' } });
  expect(field).toHaveValue('-12.34');
});
