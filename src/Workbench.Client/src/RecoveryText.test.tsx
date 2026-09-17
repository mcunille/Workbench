import { fireEvent, render, screen } from '@testing-library/react';
import { RecoveryText } from './RecoveryText';
import { recoveryText } from './formatRecoveryText';

it('selects readonly recovery text without changing it', () => {
  // GIVEN business fields containing multiline notes, arrays and a zero amount.
  const text = recoveryText({ title: 'Research', notes: 'First\nSecond', entries: [{ description: 'Stone', price: '0', sourceLink: 'https://example.test/stone' }] });
  render(<RecoveryText label="Purchase draft" text={text} />);
  // WHEN choosing the native selection action.
  fireEvent.click(screen.getByRole('button', { name: 'Select purchase draft text' }));
  // THEN focus and the complete selection allow keyboard copy without clipboard permissions.
  const field = screen.getByRole('textbox') as HTMLTextAreaElement;
  expect(field).toHaveFocus();
  expect(field).toHaveAttribute('readonly');
  expect(field).not.toBeDisabled();
  expect(field.selectionStart).toBe(0);
  expect(field.selectionEnd).toBe(text.length);
  expect(text).toContain('Notes: First\nSecond');
  expect(text).toContain('Price: 0');
  expect(text).toContain('Source Link: https://example.test/stone');
});
