import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { DetailEditor } from './DetailEditor';
import {
  getItem,
  updateItem,
  ItemConflictError,
  ItemValidationError,
} from '../../api/items';
vi.mock('../../api/items', async (original) => ({
  ...(await original<typeof import('../../api/items')>()),
  getItem: vi.fn(),
  updateItem: vi.fn(),
}));
const item = {
  id: 'item',
  name: 'Stone',
  notes: 'Original',
  location: 'Tray',
  photo: null,
  version: 'old',
  createdAtUtc: '',
};
function setup() {
  const onSaved = vi.fn();
  const onCancel = vi.fn();
  const onDirtyChange = vi.fn();
  render(
    <DetailEditor
      item={item}
      onSaved={onSaved}
      onCancel={onCancel}
      onDirtyChange={onDirtyChange}
      onAuthLost={vi.fn()}
    />,
  );
  return { onSaved, onCancel, onDirtyChange };
}
beforeEach(() => vi.clearAllMocks());
it('retains a draft but prevents reconciliation against an archived record', async () => {
  // GIVEN a draft and another session archiving the record.
  const archived = {
    ...item,
    archivedAtUtc: '2026-09-07T01:00:00Z',
    version: 'archived',
  };
  vi.mocked(updateItem).mockRejectedValue(new ItemConflictError());
  vi.mocked(getItem).mockResolvedValue(archived);
  const { onSaved } = setup();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'My unsaved draft' },
  });
  // WHEN saving THEN retain the draft with the read-only current record.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  const discard = await screen.findByRole('button', {
    name: 'Discard draft and view archived record',
  });
  expect(screen.getByText('My unsaved draft')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Review my edits' }),
  ).not.toBeInTheDocument();
  // WHEN explicitly discarding THEN display the authoritative archived record.
  fireEvent.click(discard);
  expect(onSaved).toHaveBeenCalledWith(archived);
});
it('keeps the exact checked command after an uncertain save and retries it', async () => {
  // GIVEN a save whose response is lost.
  vi.mocked(updateItem)
    .mockRejectedValueOnce(new TypeError('offline'))
    .mockResolvedValueOnce({ ...item, name: 'Blue', version: 'next' });
  const { onSaved, onDirtyChange } = setup();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'Blue' },
  });
  // WHEN saving and retrying THEN both requests use the original token and text.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('button', { name: 'Retry save' });
  expect(screen.getByLabelText('Name')).toBeDisabled();
  expect(onDirtyChange).toHaveBeenLastCalledWith(true, true);
  fireEvent.click(screen.getByRole('button', { name: 'Retry save' }));
  await waitFor(() => expect(onSaved).toHaveBeenCalledOnce());
  expect(vi.mocked(updateItem).mock.calls[0]).toEqual([
    'item',
    {
      expectedVersion: 'old',
      name: 'Blue',
      notes: 'Original',
      location: 'Tray',
    },
  ]);
  expect(vi.mocked(updateItem).mock.calls[1]).toEqual(
    vi.mocked(updateItem).mock.calls[0],
  );
});
it('preserves conflicting drafts through failed reload and reconciles from saved values', async () => {
  // GIVEN another session saved and the first conflict refresh fails.
  vi.mocked(updateItem).mockRejectedValue(new ItemConflictError());
  vi.mocked(getItem)
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValue({
      ...item,
      name: 'Current',
      notes: 'New notes',
      version: 'new',
    });
  setup();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'My draft' },
  });
  // WHEN saving THEN a fresh save requires successful reload and explicit reconciliation.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('button', { name: 'Retry loading current record' });
  expect(
    screen.queryByRole('button', { name: 'Save changes' }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('My draft')).toBeVisible();
  fireEvent.click(
    screen.getByRole('button', { name: 'Retry loading current record' }),
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Review my edits' }),
  );
  expect(screen.getByLabelText('Name')).toHaveValue('Current');
  expect(screen.getByLabelText('Notes (optional)')).toHaveValue('New notes');
  expect(screen.getByText('My draft')).toBeVisible();
  // WHEN explicitly saving again THEN a further conflict repeats review.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('button', { name: 'Review my edits' });
  expect(vi.mocked(updateItem).mock.calls[1][1]).toEqual({
    expectedVersion: 'new',
    name: 'Current',
    notes: 'New notes',
    location: 'Tray',
  });
});
it('retains editable text and focuses authoritative validation errors', async () => {
  // GIVEN authoritative field validation.
  vi.mocked(updateItem).mockRejectedValueOnce(
    new ItemValidationError({ Name: ['Name is required.'] }),
  );
  setup();
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: '' } });
  // WHEN saving THEN the invalid field is editable and focused.
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Name is required.');
  expect(screen.getByLabelText('Name')).toHaveFocus();
  expect(screen.getByLabelText('Name')).not.toBeDisabled();
});
it('cancels an unsent edit without a write', () => {
  // GIVEN an unsent draft WHEN cancelling THEN no request is sent.
  const { onCancel } = setup();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'Draft' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(onCancel).toHaveBeenCalledOnce();
  expect(updateItem).not.toHaveBeenCalled();
});

it('reloads an uncertain save before explicitly using the saved record', async () => {
  // GIVEN an ambiguous save and a later authoritative saved record.
  vi.mocked(updateItem).mockRejectedValueOnce(new Error('response lost'));
  vi.mocked(getItem).mockResolvedValueOnce({
    ...item,
    name: 'Saved elsewhere',
    version: 'new',
  });
  const { onSaved, onCancel } = setup();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'Private draft' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  // WHEN reviewing THEN returning to details uses the server record, without claiming cancellation reversed the save.
  fireEvent.click(
    await screen.findByRole('button', { name: 'Review current record' }),
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Use saved record' }),
  );
  expect(onSaved).toHaveBeenCalledWith(
    expect.objectContaining({ name: 'Saved elsewhere', version: 'new' }),
  );
  expect(onCancel).not.toHaveBeenCalled();
});
it('suppresses duplicate submissions and invalidates after a late completion', async () => {
  // GIVEN a pending command and navigation away from the mounted editor.
  let finish!: (result: typeof item) => void;
  vi.mocked(updateItem).mockReturnValueOnce(
    new Promise((resolve) => {
      finish = resolve;
    }),
  );
  const invalidate = vi.fn();
  const onSaved = vi.fn();
  const view = render(
    <DetailEditor
      item={item}
      onSaved={onSaved}
      onCancel={vi.fn()}
      onDirtyChange={vi.fn()}
      onAuthLost={vi.fn()}
      onRecordMayHaveChanged={invalidate}
    />,
  );
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  fireEvent.click(screen.getByRole('button', { name: 'Retry save' }));
  expect(updateItem).toHaveBeenCalledOnce();
  view.unmount();
  // WHEN the server completes THEN old cache is invalidated without restoring private editor state.
  finish({ ...item, version: 'new' });
  await waitFor(() => expect(invalidate).toHaveBeenCalledOnce());
  expect(onSaved).not.toHaveBeenCalled();
});
