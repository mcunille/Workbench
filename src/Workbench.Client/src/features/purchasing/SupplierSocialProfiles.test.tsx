import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { SupplierSocialProfiles } from './SupplierSocialProfiles';

it('focuses a new label before the user can move on to its handle', () => {
  // GIVEN an empty profile editor.
  function Form() {
    const [profiles, setProfiles] = useState<{ label: string; handle: string }[]>([]);
    return <SupplierSocialProfiles profiles={profiles} disabled={false} errors={{}} onChange={setProfiles} />;
  }
  render(<Form />);
  // WHEN adding a row THEN focus is immediately ready for entry, without a delayed focus change.
  fireEvent.click(screen.getByRole('button', { name: 'Add social' }));
  expect(screen.getByRole('textbox', { name: 'Platform' })).toHaveFocus();
});

it('keeps focus on an available control after removing a row at the limit', async () => {
  // GIVEN the maximum number of reference handles, with Add social disabled.
  function Form() {
    const [profiles, setProfiles] = useState(Array.from({ length: 20 }, (_, index) => ({ label: `Social ${index}`, handle: `@gems${index}` })));
    return <SupplierSocialProfiles profiles={profiles} disabled={false} errors={{}} onChange={setProfiles} />;
  }
  render(<Form />);
  expect(screen.getByRole('button', { name: 'Add social' })).toBeDisabled();
  // WHEN removing the final row THEN adding is available again and focus stays within the editor.
  fireEvent.click(screen.getByRole('button', { name: 'Remove social 20' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Add social' })).toHaveFocus());
  expect(screen.getAllByRole('textbox')).toHaveLength(38);
});

it('freezes all profile actions while a save is pending', () => {
  // GIVEN an in-flight save WHEN the profile editor is frozen THEN its values and list cannot change.
  const onChange = vi.fn();
  render(<SupplierSocialProfiles profiles={[{ label: 'Discord', handle: '@gems' }]} disabled errors={{}} onChange={onChange} />);
  for (const control of [...screen.getAllByRole('textbox'), ...screen.getAllByRole('button')]) expect(control).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Remove social 1' }));
  fireEvent.click(screen.getByRole('button', { name: 'Add social' }));
  expect(onChange).not.toHaveBeenCalled();
});
