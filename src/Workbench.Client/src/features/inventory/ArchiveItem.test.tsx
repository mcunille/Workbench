import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { ItemDetails } from './Collection';
import { CollectionMemory } from './collectionMemory';
import { getItem, archiveItem, ItemConflictError } from '../../api/items';
vi.mock('../../api/items', async (original) => ({
  ...(await original<typeof import('../../api/items')>()),
  getItem: vi.fn(),
  archiveItem: vi.fn(),
}));
const item = {
  id: 'item',
  name: 'Stone',
  notes: 'Keep',
  location: 'Tray',
  photo: null,
  version: 'old',
  createdAtUtc: '2026-09-07T00:00:00Z',
  archivedAtUtc: null,
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getItem).mockResolvedValue(item);
});
function setup() {
  const dirty = vi.fn();
  render(
    <ItemDetails
      id="item"
      follow={vi.fn()}
      onAuthLost={vi.fn()}
      onDirtyChange={dirty}
    />,
  );
  return dirty;
}
it('cancels confirmation without a request and restores focus', async () => {
  // GIVEN an active record.
  setup();
  const trigger = await screen.findByRole('button', {
    name: 'Archive record',
  });
  trigger.focus();
  // WHEN opening and cancelling confirmation.
  fireEvent.click(trigger);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  // THEN no archive was requested and keyboard focus returns.
  expect(archiveItem).not.toHaveBeenCalled();
  await waitFor(() => expect(trigger).toHaveFocus());
});
it('retries an uncertain archive with the original version and shows read-only saved state', async () => {
  // GIVEN a response lost after submission.
  vi.mocked(archiveItem)
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValueOnce({
      ...item,
      version: 'new',
      archivedAtUtc: '2026-09-07T01:00:00Z',
    });
  const dirty = setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Archive record' }),
  );
  // WHEN confirming and retrying.
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm archive record' }),
  );
  const retryArchive = await screen.findByRole('button', {
    name: 'Retry archive',
  });
  await waitFor(() => expect(retryArchive).toBeEnabled());
  fireEvent.click(retryArchive);
  // THEN the original command is retained and mutation controls disappear.
  await screen.findByText('Archived');
  expect(vi.mocked(archiveItem).mock.calls).toEqual([
    ['item', { expectedVersion: 'old' }],
    ['item', { expectedVersion: 'old' }],
  ]);
  expect(
    screen.queryByRole('button', { name: 'Edit details' }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByLabelText('Choose photograph'),
  ).not.toBeInTheDocument();
  expect(dirty).toHaveBeenLastCalledWith(false, false);
});
it('requires renewed confirmation after a stale archive and a failed recovery read', async () => {
  // GIVEN a conflict followed by one unavailable recovery read.
  vi.mocked(archiveItem).mockRejectedValue(new ItemConflictError());
  vi.mocked(getItem)
    .mockResolvedValueOnce(item)
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValue({ ...item, name: 'Changed', version: 'new' });
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Archive record' }),
  );
  // WHEN confirming and recovering current details.
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm archive record' }),
  );
  const retryLoading = await screen.findByRole('button', {
    name: 'Retry loading current record',
  });
  await waitFor(() => expect(retryLoading).toBeEnabled());
  fireEvent.click(retryLoading);
  // THEN no fresh command is silently sent and a new confirmation is required.
  await screen.findByRole('heading', { name: 'Changed' });
  expect(archiveItem).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('button', { name: 'Archive record' }));
  expect(
    screen.getByRole('button', { name: 'Confirm archive record' }),
  ).toBeVisible();
});
it('blocks duplicates and other editors while pending and invalidates uncertain collection pages', async () => {
  // GIVEN a cached search and an archive request still in flight.
  let reject!: (error: Error) => void;
  vi.mocked(archiveItem).mockReturnValue(
    new Promise((_, failure) => {
      reject = failure;
    }),
  );
  const memory = new CollectionMemory();
  memory.save({
    query: 'Stone',
    draft: 'Stone',
    view: 'list',
    page: { items: [item], nextCursor: 'old' },
  });
  const dirty = vi.fn();
  render(
    <ItemDetails
      id="item"
      memory={memory}
      follow={vi.fn()}
      onAuthLost={vi.fn()}
      onDirtyChange={dirty}
    />,
  );
  // WHEN confirming twice THEN only one request is sent and navigation is marked uncertain.
  fireEvent.click(
    await screen.findByRole('button', { name: 'Archive record' }),
  );
  expect(screen.getByLabelText('Choose photograph')).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Edit details' })).toBeDisabled();
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm archive record' }),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Retry archive' }));
  expect(archiveItem).toHaveBeenCalledOnce();
  expect(dirty).toHaveBeenLastCalledWith(true, true);
  // WHEN the response is lost THEN stale pages are removed while search and view survive.
  reject(new Error('offline'));
  await screen.findByRole('alert');
  expect(memory.snapshot).toEqual({
    query: 'Stone',
    draft: 'Stone',
    view: 'list',
    page: undefined,
  });
});
it('shows an already archived conflict as current saved state without claiming request success', async () => {
  // GIVEN another session has archived the record before this command succeeds.
  vi.mocked(archiveItem).mockRejectedValue(new ItemConflictError());
  vi.mocked(getItem)
    .mockResolvedValueOnce(item)
    .mockResolvedValue({
      ...item,
      version: 'archived',
      archivedAtUtc: '2026-09-07T01:00:00Z',
    });
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Archive record' }),
  );
  // WHEN a conflict is recovered THEN show the saved archived state without attribution.
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm archive record' }),
  );
  await screen.findByText('Archived');
  expect(screen.getByText('Current saved record loaded.')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Archive record' }),
  ).not.toBeInTheDocument();
});
it('opens an archived bookmark as read-only and removes cached active matches', async () => {
  // GIVEN a direct link to an archived record and an old active search page.
  vi.mocked(getItem).mockResolvedValue({
    ...item,
    archivedAtUtc: '2026-09-07T01:00:00Z',
  });
  const memory = new CollectionMemory();
  memory.save({
    query: 'Stone',
    draft: 'Stone',
    view: 'grid',
    page: { items: [item], nextCursor: 'old' },
  });
  // WHEN opening its link THEN retain identity and text, hide mutation controls and invalidate browsing.
  render(
    <ItemDetails
      id="item"
      memory={memory}
      follow={vi.fn()}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
    />,
  );
  await screen.findByText('Archived');
  expect(screen.getByText('Keep')).toBeVisible();
  expect(screen.getByText('item')).toBeVisible();
  expect(
    screen.queryByLabelText('Choose photograph'),
  ).not.toBeInTheDocument();
  expect(memory.snapshot?.page).toBeUndefined();
});
