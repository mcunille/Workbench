import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { vi } from 'vitest';
import { Collection, ItemDetails } from './Collection';
import { getArchivedItems, getItem, getItems } from '../../api/items';
import { CollectionMemory } from './collectionMemory';
import { ApiError } from '../../api/auth';

const summary = (id: string) => ({
  id,
  name: id,
  location: null,
  photo: null,
  createdAtUtc: '2026-09-06T00:00:00Z',
});

it('discards an outdated response and retries the same matching page after a failure', async () => {
  // GIVEN a previous collection request that has not completed.
  let finishOld!: (page: Awaited<ReturnType<typeof getItems>>) => void;
  vi.mocked(getItems)
    .mockReturnValueOnce(
      new Promise((resolve) => {
        finishOld = resolve;
      }),
    )
    .mockResolvedValueOnce({
      items: [summary('Match')],
      nextCursor: 'matching-next',
    })
    .mockRejectedValueOnce(new ApiError(500))
    .mockResolvedValueOnce({
      items: [summary('Later match')],
      nextCursor: null,
    });
  render(<Collection follow={vi.fn()} onAuthLost={vi.fn()} />);
  // WHEN a newer search finishes before the previous response.
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'match' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('link', { name: /Match/ });
  await act(async () => {
    finishOld({ items: [summary('Outdated')], nextCursor: null });
  });
  // THEN only matching results remain, and a failed next page keeps those results.
  expect(
    screen.queryByRole('link', { name: /Outdated/ }),
  ).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
  await screen.findByRole('alert');
  expect(screen.getByRole('link', { name: /Match/ })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByRole('link', { name: /Later match/ });
  expect(vi.mocked(getItems).mock.calls.slice(-2)).toEqual([
    ['matching-next', 'match'],
    ['matching-next', 'match'],
  ]);
});

it('distinguishes no matches and reports server validation without showing prior results', async () => {
  // GIVEN an unfiltered collection and an authoritative search validation failure.
  vi.mocked(getItems)
    .mockResolvedValueOnce({ items: [summary('Old')], nextCursor: null })
    .mockRejectedValueOnce(new ApiError(400))
    .mockResolvedValueOnce({ items: [], nextCursor: null });
  render(<Collection follow={vi.fn()} onAuthLost={vi.fn()} />);
  await screen.findByRole('link', { name: /Old/ });
  // WHEN the server rejects a query THEN the prior results are removed.
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'rejected' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(/200/);
  expect(screen.queryByRole('link', { name: /Old/ })).not.toBeInTheDocument();
  // WHEN a valid search has no results THEN distinguish it from an empty collection.
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'absent' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await waitFor(() =>
    expect(screen.getByRole('status')).toHaveTextContent(/No matches/),
  );
  expect(
    screen.queryByText('Your collection starts here'),
  ).not.toBeInTheDocument();
});

vi.mock('../../api/items', () => ({
  getItems: vi.fn(),
  getItem: vi.fn(),
  getArchivedItems: vi.fn(),
}));
beforeEach(() => {
  vi.mocked(getItems).mockReset();
  vi.mocked(getItem).mockReset();
  vi.mocked(getArchivedItems).mockReset();
});

it('opens the add-item flow from the whole empty collection card', async () => {
  // GIVEN an empty collection.
  vi.mocked(getItems).mockResolvedValue({ items: [], nextCursor: null });
  const follow = vi.fn((event) => event.preventDefault());
  render(<Collection follow={follow} onAuthLost={vi.fn()} />);
  const card = await screen.findByRole('link', {
    name: /Your collection starts here/,
  });
  // WHEN clicking the explanatory text inside the card.
  fireEvent.click(screen.getByText(/Add your first item with just a name/));
  // THEN the card uses the existing add-item route and navigation handler.
  expect(card).toHaveAttribute('href', '/inventory/new');
  expect(follow).toHaveBeenCalledTimes(1);
});

it('keeps the empty archive informational', async () => {
  // GIVEN an empty archive.
  vi.mocked(getArchivedItems).mockResolvedValue({ items: [], nextCursor: null });
  const follow = vi.fn();
  render(<Collection archived follow={follow} onAuthLost={vi.fn()} />);
  // WHEN clicking its empty-state heading.
  fireEvent.click(
    await screen.findByRole('heading', { name: 'Your archive is empty' }),
  );
  // THEN it does not offer or trigger item creation.
  expect(
    screen.queryByRole('link', { name: /Your archive is empty/ }),
  ).not.toBeInTheDocument();
  expect(follow).not.toHaveBeenCalled();
});

it('removes an unavailable selected item and returns focus to the collection heading', async () => {
  // GIVEN a cached item that is no longer available on the server.
  const memory = new CollectionMemory();
  memory.save({
    view: 'list',
    query: 'stone',
    draft: 'stone',
    page: { items: [summary('Gone')], nextCursor: null },
  });
  memory.select('Gone');
  vi.mocked(getItem).mockRejectedValue(new ApiError(404));
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  const details = render(
    <ItemDetails
      id="Gone"
      memory={memory}
      follow={vi.fn()}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
    />,
  );
  await screen.findByRole('heading', { name: 'Item not found' });
  // WHEN returning to the collection THEN the dead link is gone and focus has a safe fallback.
  details.unmount();
  render(<Collection memory={memory} follow={vi.fn()} onAuthLost={vi.fn()} />);
  expect(screen.queryByRole('link', { name: /Gone/ })).not.toBeInTheDocument();
  expect(screen.getByRole('heading', { name: 'Collection' })).toHaveFocus();
  expect(screen.getByRole('searchbox')).toHaveValue('stone');
});

it('restores the submitted traversal, unfinished draft, selection, and scroll after details', async () => {
  // GIVEN a matching traversal with two loaded pages and an unfinished next query.
  const memory = new CollectionMemory();
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.mocked(getItems)
    .mockResolvedValueOnce({ items: [summary('First')], nextCursor: 'next' })
    .mockResolvedValueOnce({ items: [summary('First')], nextCursor: 'next' })
    .mockResolvedValueOnce({ items: [summary('Second')], nextCursor: null });
  const props = { follow: vi.fn(), onAuthLost: vi.fn(), memory };
  const initial = render(<Collection {...props} />);
  await screen.findByRole('link', { name: /First/ });
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'stone' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('link', { name: /First/ });
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
  await screen.findByRole('link', { name: /Second/ });
  fireEvent.change(screen.getByRole('searchbox'), {
    target: { value: 'unfinished' },
  });
  fireEvent.click(screen.getByRole('link', { name: /Second/ }));
  memory.scrollY = 650;
  // WHEN returning from details THEN restore existing rows before focus and position.
  initial.unmount();
  render(<Collection {...props} />);
  expect(screen.getByRole('searchbox')).toHaveValue('unfinished');
  expect(
    screen.getByRole('button', { name: 'List', pressed: true }),
  ).toBeVisible();
  expect(screen.getByRole('link', { name: /Second/ })).toHaveFocus();
  expect(screen.getByRole('link', { name: /First/ })).toBeVisible();
  expect(scroll).toHaveBeenLastCalledWith({ top: 650, behavior: 'instant' });
  expect(getItems).toHaveBeenCalledTimes(3);
});

it('submits searches only on request and clears them while retaining List view', async () => {
  // GIVEN a loaded collection in List view.
  vi.mocked(getItems).mockResolvedValue({
    items: [
      {
        id: 'one',
        name: 'Stone',
        location: null,
        photo: null,
        createdAtUtc: '2026-09-06T00:00:00Z',
      },
    ],
    nextCursor: null,
  });
  render(<Collection follow={vi.fn()} onAuthLost={vi.fn()} />);
  await screen.findByRole('link', { name: /Stone/ });
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  // WHEN entering text and submitting it explicitly.
  fireEvent.change(
    screen.getByRole('searchbox', { name: 'Search collection' }),
    { target: { value: '  BLUE % stone  ' } },
  );
  expect(getItems).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  await screen.findByRole('link', { name: /Stone/ });
  expect(getItems).toHaveBeenLastCalledWith(undefined, 'BLUE % stone');
  // THEN Clear loads all items while preserving the selected view.
  fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
  await screen.findByRole('link', { name: /Stone/ });
  expect(getItems).toHaveBeenLastCalledWith(undefined, undefined);
  expect(
    screen.getByRole('button', { name: 'List', pressed: true }),
  ).toBeVisible();
});

it('switches collection presentation without losing loaded records or fetching again', async () => {
  // GIVEN an existing collection with a second page available.
  vi.mocked(getItems).mockResolvedValue({
    items: [
      {
        id: 'sapphire',
        photo: null,
        name: 'Blue sapphire',
        location: null,
        createdAtUtc: '2026-09-06T00:00:00Z',
      },
    ],
    nextCursor: 'next',
  });
  render(<Collection follow={vi.fn()} onAuthLost={vi.fn()} />);
  const link = await screen.findByRole('link', { name: /Blue sapphire/ });
  expect(
    screen.getByRole('button', { name: 'Grid', pressed: true }),
  ).toBeVisible();
  // WHEN the collector selects the compact list.
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  // THEN the same link and pagination remain, with only presentation changed.
  expect(
    screen.getByRole('button', { name: 'List', pressed: true }),
  ).toBeVisible();
  expect(link).toHaveAttribute('href', '/inventory/sapphire');
  expect(link.closest('ul')).toHaveAttribute('data-view', 'list');
  expect(screen.getByRole('button', { name: 'Load more' })).toBeEnabled();
  expect(getItems).toHaveBeenCalledTimes(1);
  // WHEN returning to the gallery, the record remains available.
  fireEvent.click(screen.getByRole('button', { name: 'Grid' }));
  expect(link.closest('ul')).toHaveAttribute('data-view', 'grid');
  expect(getItems).toHaveBeenCalledTimes(1);
});

it('hides the prior item and its photo editor while a different item loads', async () => {
  // GIVEN one loaded item and a pending fetch for the next item.
  vi.mocked(getItem)
    .mockResolvedValueOnce({
      id: 'first',
      name: 'First stone',
      notes: null,
      location: null,
      photo: null,
      version: 'v',
      createdAtUtc: '2026-09-06T00:00:00Z',
    })
    .mockReturnValueOnce(new Promise(() => {}));
  const props = {
    follow: vi.fn(),
    onAuthLost: vi.fn(),
    onDirtyChange: vi.fn(),
  };
  const view = render(<ItemDetails id="first" {...props} />);
  await screen.findByRole('heading', { name: 'First stone' });
  // WHEN the route changes THEN no photo command can target the previous item.
  view.rerender(<ItemDetails id="second" {...props} />);
  expect(
    screen.queryByRole('heading', { name: 'First stone' }),
  ).not.toBeInTheDocument();
  expect(screen.queryByLabelText('Choose photograph')).not.toBeInTheDocument();
});
