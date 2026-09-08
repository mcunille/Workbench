import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { vi } from 'vitest';
import { Collection, ItemDetails } from './Collection';
import { CollectionMemory } from './collectionMemory';
import {
  getItem,
  getArchivedItems,
  restoreItem,
  ItemConflictError,
} from '../../api/items';
vi.mock('../../api/items', async (original) => ({
  ...(await original<typeof import('../../api/items')>()),
  getItem: vi.fn(),
  getArchivedItems: vi.fn(),
  restoreItem: vi.fn(),
}));
const item = {
  id: 'stone',
  name: 'Stone',
  notes: 'Keep',
  location: 'Tray',
  photo: null,
  version: 'archived',
  createdAtUtc: '2026-09-07T00:00:00Z',
  archivedAtUtc: '2026-09-07T01:00:00Z',
};
const active = { ...item, version: 'restored', archivedAtUtc: null };
const snapshot = {
  query: 'Stone',
  draft: 'Stone',
  view: 'list' as const,
  page: { items: [item], nextCursor: 'old' },
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getItem).mockResolvedValue(item);
});
function details(
  memory = new CollectionMemory(),
  archiveMemory = new CollectionMemory(),
) {
  const dirty = vi.fn();
  const result = render(
    <ItemDetails
      id="stone"
      memory={memory}
      archiveMemory={archiveMemory}
      origin="archived"
      follow={vi.fn()}
      onAuthLost={vi.fn()}
      onDirtyChange={dirty}
    />,
  );
  return { ...result, dirty };
}
const open = async () =>
  fireEvent.click(
    await screen.findByRole('button', { name: 'Restore to collection' }),
  );
const confirm = () =>
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm restore' }),
  );
it('cancels restoration without a request and returns focus while keeping archived details read-only', async () => {
  // GIVEN an archived direct record.
  details();
  // WHEN confirmation is opened and cancelled.
  await open();
  expect(
    screen.getByRole('heading', { name: 'Restore to collection?' }),
  ).toHaveFocus();
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  // THEN focus returns and neither a restore nor editing is available implicitly.
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Restore to collection' }),
    ).toHaveFocus(),
  );
  expect(restoreItem).not.toHaveBeenCalled();
  expect(
    screen.queryByRole('button', { name: 'Edit details' }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByLabelText('Choose photograph'),
  ).not.toBeInTheDocument();
});
it('retries the original token, blocks duplicates, and invalidates both traversals on a late completion', async () => {
  // GIVEN cached active/archive searches and a submitted restore whose response is lost.
  const memory = new CollectionMemory();
  const archived = new CollectionMemory();
  memory.save({ ...snapshot, page: { items: [], nextCursor: null } });
  archived.save(snapshot);
  let finish!: (value: typeof active) => void;
  vi.mocked(restoreItem)
    .mockRejectedValueOnce(new Error('offline'))
    .mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
  const { dirty, unmount } = details(memory, archived);
  await open();
  confirm();
  await screen.findByRole('alert');
  expect(memory.snapshot?.page).toBeUndefined();
  expect(archived.snapshot?.page).toBeUndefined();
  expect(dirty).toHaveBeenLastCalledWith(true, true);
  // WHEN retrying twice then navigating away before a successful response.
  fireEvent.click(screen.getByRole('button', { name: 'Retry restore' }));
  fireEvent.click(screen.getByRole('button', { name: 'Retry restore' }));
  expect(vi.mocked(restoreItem).mock.calls).toEqual([
    ['stone', { expectedVersion: 'archived' }],
    ['stone', { expectedVersion: 'archived' }],
  ]);
  unmount();
  memory.save(snapshot);
  archived.save(snapshot);
  await act(async () => finish(active));
  // THEN even a late completion removes stale pages while retaining independent preferences.
  expect(memory.snapshot).toEqual({ ...snapshot, page: undefined });
  expect(archived.snapshot).toEqual({ ...snapshot, page: undefined });
});
it('requires a new explicit confirmation after a re-archive conflict and failed recovery read', async () => {
  // GIVEN an intervening restore/re-archive cycle and a recovery read failure.
  vi.mocked(restoreItem)
    .mockRejectedValueOnce(new ItemConflictError())
    .mockResolvedValue(active);
  vi.mocked(getItem)
    .mockResolvedValueOnce(item)
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValue({ ...item, version: 'rearchived' });
  details();
  await open();
  confirm();
  // WHEN the current record is reloaded THEN no command is retried against its new token automatically.
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Retry loading current record',
    }),
  );
  await screen.findByRole('button', { name: 'Restore to collection' });
  expect(restoreItem).toHaveBeenCalledOnce();
  await open();
  confirm();
  // THEN a new confirmed command uses the new token and shows active editing with an archive return path.
  await screen.findByText('Record restored to collection.');
  expect(restoreItem).toHaveBeenLastCalledWith('stone', {
    expectedVersion: 'rearchived',
  });
  expect(
    screen.getByRole('button', { name: 'Edit details' }),
  ).toBeVisible();
  expect(
    screen.getByRole('link', { name: 'Back to archive' }),
  ).toHaveAttribute('href', '/inventory/archive');
  expect(
    screen.getByRole('link', { name: 'View in collection' }),
  ).toBeVisible();
});
it('reviews already-active state without claiming this restore succeeded', async () => {
  // GIVEN a restore completed in another session.
  vi.mocked(restoreItem).mockRejectedValue(new ItemConflictError());
  vi.mocked(getItem)
    .mockResolvedValueOnce(item)
    .mockResolvedValue(active);
  details();
  await open();
  confirm();
  // THEN conflict review explains the saved membership, without a success attribution.
  await screen.findByText(
    'This record is already in the collection. Current saved record loaded.',
  );
  expect(
    screen.queryByText('Record restored to collection.'),
  ).not.toBeInTheDocument();
  expect(restoreItem).toHaveBeenCalledOnce();
});
it('searches the archive, retries its matching continuation and preserves archive position through details', async () => {
  // GIVEN the archive has multiple matching pages and a later-page failure.
  vi.mocked(getArchivedItems)
    .mockResolvedValueOnce({ items: [item], nextCursor: 'next' })
    .mockResolvedValueOnce({ items: [item], nextCursor: 'next' })
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValueOnce({
      items: [{ ...item, id: 'later', name: 'Later stone' }],
      nextCursor: null,
    });
  const archiveMemory = new CollectionMemory();
  const memory = new CollectionMemory();
  const props = {
    archived: true,
    memory: archiveMemory,
    follow: vi.fn(),
    onAuthLost: vi.fn(),
  };
  const list = render(<Collection {...props} />);
  await screen.findByRole('link', { name: /Stone/ });
  fireEvent.change(
    screen.getByRole('searchbox', { name: 'Search archive' }),
    {
      target: { value: 'Stone' },
    },
  );
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('link', { name: /Stone/ });
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
  await screen.findByRole('alert');
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByRole('link', { name: /Later stone/ });
  expect(vi.mocked(getArchivedItems).mock.calls.slice(-2)).toEqual([
    ['next', 'Stone'],
    ['next', 'Stone'],
  ]);
  archiveMemory.select('stone');
  archiveMemory.scrollY = 650;
  list.unmount();
  const detail = details(memory, archiveMemory);
  await screen.findByText('Archived');
  detail.unmount();
  // WHEN returning from an unchanged archived detail THEN no archive page or position was lost.
  render(<Collection {...props} />);
  expect(screen.getByRole('searchbox')).toHaveValue('Stone');
  expect(
    screen.getByRole('button', { name: 'List', pressed: true }),
  ).toBeVisible();
  expect(screen.getByRole('link', { name: /Later stone/ })).toBeVisible();
  expect(getArchivedItems).toHaveBeenCalledTimes(4);
});
it('distinguishes an empty archive, no matches, loading failure and a successful retry', async () => {
  // GIVEN an empty archive followed by a no-match search and an unavailable server.
  vi.mocked(getArchivedItems)
    .mockResolvedValueOnce({ items: [], nextCursor: null })
    .mockResolvedValueOnce({ items: [], nextCursor: null })
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValueOnce({ items: [item], nextCursor: null });
  render(<Collection archived follow={vi.fn()} onAuthLost={vi.fn()} />);
  await screen.findByRole('heading', { name: 'Your archive is empty' });
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'missing' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByText('No matches');
  fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'We could not load the archive',
  );
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByRole('link', { name: /Stone/ });
});
