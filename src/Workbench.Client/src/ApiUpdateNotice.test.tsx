import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { ApiUpdateNotice } from './ApiUpdateNotice';
import { createApiTransport } from './api/contract';

afterEach(() => vi.unstubAllGlobals());

it('announces a reload requirement without replacing the form or losing the user’s edits', async () => {
  // GIVEN an open form with unsaved input and a server using an incompatible contract.
  const contract = createApiTransport();
  function Page() {
    const [notes, setNotes] = useState('');
    return <><ApiUpdateNotice contract={contract} /><label>Notes<input value={notes} onChange={event => setNotes(event.target.value)} /></label></>;
  }
  render(<Page />);
  fireEvent.change(screen.getByRole('textbox', { name: 'Notes' }), { target: { value: 'Keep my edits' } });
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ code: 'api_contract_unsupported' }, { status: 409 })));
  // WHEN the incompatible response reaches the mounted page.
  await act(async () => { await expect(contract.fetch('http://localhost/api/beta/items')).rejects.toThrow(); });
  // THEN recovery guidance is announced and the existing input remains available for copying.
  expect(screen.getByRole('alert')).toHaveTextContent('Copy them before reloading');
  expect(screen.getByRole('alert')).toHaveTextContent('keep this page open');
  expect(screen.getByRole('textbox', { name: 'Notes' })).toHaveValue('Keep my edits');
});
