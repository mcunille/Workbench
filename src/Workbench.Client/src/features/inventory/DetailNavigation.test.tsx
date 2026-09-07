import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { ItemDetails, Collection } from './Collection';
import { CollectionMemory } from './collectionMemory';
import { getItem, getItems, updateItem } from '../../api/items';
vi.mock('../../api/items', async (original) => ({
  ...(await original<typeof import('../../api/items')>()),
  getItem: vi.fn(),
  getItems: vi.fn(),
  updateItem: vi.fn(),
}));
const item = {
  id: 'stone',
  name: 'Stone',
  notes: null,
  location: 'Tray',
  photo: null,
  version: 'old',
  createdAtUtc: '',
};
it('isolates details editing and reloads search membership while retaining the view', async () => {
  // GIVEN cached matching pages and an item detail.
  const memory = new CollectionMemory();
  memory.save({
    query: 'Stone',
    draft: 'Stone',
    view: 'list',
    page: { items: [item], nextCursor: 'old' },
  });
  vi.mocked(getItem).mockResolvedValue(item);
  vi.mocked(updateItem).mockResolvedValue({
    ...item,
    name: 'Ruby',
    version: 'new',
  });
  vi.mocked(getItems).mockResolvedValue({ items: [], nextCursor: null });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  const view = render(
    <ItemDetails
      id="stone"
      memory={memory}
      onDirtyChange={vi.fn()}
      onAuthLost={vi.fn()}
      follow={vi.fn()}
    />,
  );
  // WHEN editing THEN photograph mutation controls are unavailable.
  fireEvent.click(await screen.findByRole('button', { name: 'Edit details' }));
  expect(screen.queryByLabelText('Choose photograph')).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'Ruby' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('heading', { name: 'Ruby' });
  expect(screen.getByLabelText('Choose photograph')).toBeEnabled();
  // WHEN returning to the collection THEN obsolete matches and pagination are reloaded from the start.
  view.unmount();
  render(<Collection memory={memory} onAuthLost={vi.fn()} follow={vi.fn()} />);
  await screen.findByRole('heading', { name: 'No matches' });
  expect(getItems).toHaveBeenCalledWith(undefined, 'Stone');
  expect(memory.snapshot?.view).toBe('list');
  expect(screen.queryByRole('link', { name: /Stone/ })).not.toBeInTheDocument();
});
it('restarts a mounted collection after a late detail mutation invalidates it', async () => {
  // GIVEN a mounted collection showing an old retained page.
  const memory = new CollectionMemory();
  memory.save({
    query: '',
    draft: '',
    view: 'grid',
    page: { items: [item], nextCursor: 'old' },
  });
  vi.mocked(getItems)
    .mockReset()
    .mockResolvedValue({
      items: [{ ...item, name: 'Updated' }],
      nextCursor: null,
    });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  render(<Collection memory={memory} onAuthLost={vi.fn()} follow={vi.fn()} />);
  // WHEN a late mutation finishes THEN the visible list refreshes without retaining the old page boundary.
  fireEvent.click(screen.getByRole('button', { name: 'Grid' }));
  await import('@testing-library/react').then(({ act }) =>
    act(() => memory.invalidate()),
  );
  await screen.findByRole('link', { name: /Updated/ });
  await waitFor(() =>
    expect(
      screen.queryByRole('button', { name: 'Load more' }),
    ).not.toBeInTheDocument(),
  );
});

it('keeps the latest saved record when cancelling reconciliation', async () => {
  // GIVEN a conflicting save followed by a newer saved record.
  const { ItemConflictError } = await import('../../api/items');
  vi.mocked(getItem)
    .mockReset()
    .mockResolvedValueOnce(item)
    .mockResolvedValueOnce({
      ...item,
      name: 'Latest saved',
      notes: 'Current notes',
      version: 'latest',
    });
  vi.mocked(updateItem)
    .mockReset()
    .mockRejectedValueOnce(new ItemConflictError())
    .mockResolvedValueOnce({ ...item, name: 'Latest saved', version: 'after' });
  render(
    <ItemDetails
      id="stone"
      onDirtyChange={vi.fn()}
      onAuthLost={vi.fn()}
      follow={vi.fn()}
    />,
  );
  fireEvent.click(await screen.findByRole('button', { name: 'Edit details' }));
  fireEvent.change(screen.getByLabelText('Name'), {
    target: { value: 'Private stale draft' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  fireEvent.click(
    await screen.findByRole('button', { name: 'Review my edits' }),
  );
  // WHEN cancelling reconciliation THEN details retain the latest saved values.
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  await screen.findByRole('heading', { name: 'Latest saved' });
  expect(screen.getByText('Current notes')).toBeVisible();
  expect(updateItem).toHaveBeenCalledOnce();
  // AND a later editor captures the latest version rather than the original stale one.
  fireEvent.click(screen.getByRole('button', { name: 'Edit details' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() =>
    expect(updateItem).toHaveBeenLastCalledWith(
      'stone',
      expect.objectContaining({
        expectedVersion: 'latest',
        name: 'Latest saved',
      }),
    ),
  );
});
