import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { AddItem } from './AddItem';
import { createItem, ItemValidationError } from '../../api/items';
vi.mock('../../api/items', () => ({
  createItem: vi.fn(),
  ItemValidationError: class extends Error {
    errors = { Name: ['Enter a name.'] };
  },
}));
const saved = {
  id: 'saved-id',
  name: 'Sapphire',
  notes: null,
  location: null,
  createdAtUtc: '2026-09-06T00:00:00Z',
};
const props = () => ({
  onSaved: vi.fn(),
  onCancel: vi.fn(),
  onDirtyChange: vi.fn(),
  onAuthLost: vi.fn(),
});
describe('Add item', () => {
  beforeEach(() => vi.mocked(createItem).mockReset());
  it('prevents a second submission while the first response is pending', async () => {
    // GIVEN the server has not answered the first save
    const count = vi.mocked(createItem).mock.calls.length;
    let resolve!: (value: typeof saved) => void;
    vi.mocked(createItem).mockImplementationOnce(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const callbacks = props();
    render(<AddItem {...callbacks} />);
    fireEvent.change(screen.getByLabelText('Name'), {
      target: { value: 'Sapphire' },
    });
    const form = screen.getByLabelText('Name').closest('form')!;
    // WHEN two submit events arrive before the response
    fireEvent.submit(form);
    fireEvent.submit(form);
    // THEN only one operation is sent and success waits for the response
    expect(vi.mocked(createItem).mock.calls.length).toBe(count + 1);
    expect(callbacks.onSaved).not.toHaveBeenCalled();
    resolve(saved);
    await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalledWith(saved));
  });
  it('freezes an uncertain save and retries the identical operation once', async () => {
    // GIVEN a save whose response was lost
    vi.mocked(createItem)
      .mockRejectedValueOnce(new TypeError('Network'))
      .mockResolvedValueOnce(saved);
    const callbacks = props();
    render(<AddItem {...callbacks} />);
    fireEvent.change(screen.getByLabelText('Name'), {
      target: { value: 'Sapphire' },
    });
    // WHEN the user saves, then explicitly retries
    fireEvent.click(screen.getByRole('button', { name: 'Save item' }));
    await screen.findByRole('button', { name: 'Retry save' });
    expect(screen.getByLabelText('Name')).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Retry save' }));
    // THEN the original payload and request identifier are reused
    await waitFor(() => expect(callbacks.onSaved).toHaveBeenCalledWith(saved));
    expect(vi.mocked(createItem).mock.calls[1][0]).toEqual(
      vi.mocked(createItem).mock.calls[0][0],
    );
  });
  it('preserves rejected input and focuses the invalid field', async () => {
    // GIVEN authoritative validation rejects the submission
    vi.mocked(createItem).mockRejectedValueOnce(
      new ItemValidationError({ Name: ['Enter a name.'] }),
    );
    render(<AddItem {...props()} />);
    fireEvent.change(screen.getByLabelText('Notes (optional)'), {
      target: { value: 'Keep this note' },
    });
    // WHEN saving
    fireEvent.click(screen.getByRole('button', { name: 'Save item' }));
    // THEN input is retained and the name is focused for correction
    await screen.findByText('Enter a name.');
    expect(screen.getByLabelText('Notes (optional)')).toHaveValue(
      'Keep this note',
    );
    expect(screen.getByLabelText('Name')).toHaveFocus();
    expect(screen.getByLabelText('Name')).not.toBeDisabled();
  });
});
