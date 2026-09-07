import { act, fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { Collection, ItemDetails } from './Collection';
import { CollectionMemory } from './collectionMemory';
import {
  getItem,
  getPhoto,
  removeItemPhoto,
  type ItemDetail,
} from '../../api/items';
import { ApiError } from '../../api/auth';

vi.mock('../../api/items', () => ({
  getItem: vi.fn(),
  getItems: vi.fn(),
  getPhoto: vi.fn(),
  removeItemPhoto: vi.fn(),
}));

const item: ItemDetail = {
  id: 'stone',
  name: 'Stone',
  notes: null,
  location: null,
  createdAtUtc: '',
  version: 'original',
  photo: {
    id: 'old-photo',
    thumbnailUrl: '/old/thumbnail',
    detailUrl: '/old/detail',
    width: 10,
    height: 10,
  },
};
function traversal() {
  const memory = new CollectionMemory();
  memory.save({
    draft: 'unfinished',
    query: 'stone',
    view: 'list',
    page: { items: [item], nextCursor: 'later-page' },
  });
  return memory;
}

beforeEach(() => {
  vi.mocked(getItem).mockReset().mockResolvedValue(item);
  vi.mocked(getPhoto)
    .mockReset()
    .mockResolvedValue(new Blob(['photo']));
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  HTMLDialogElement.prototype.showModal = vi.fn();
  URL.createObjectURL = vi.fn(() => 'blob:photo');
  URL.revokeObjectURL = vi.fn();
});

it.each(['success', 'failure', 'new identity'] as const)(
  'handles a late photo removal after leaving details: %s',
  async (outcome) => {
    // GIVEN a retained traversal and a photo removal that has not completed.
    const oldMemory = traversal();
    let finish!: () => void;
    let reject!: (error: Error) => void;
    vi.mocked(removeItemPhoto)
      .mockReset()
      .mockReturnValue(
        new Promise((resolve, fail) => {
          finish = () =>
            resolve({ requestId: 'removal', version: 'next', photoId: null });
          reject = fail;
        }),
      );
    const authLost = vi.fn();
    const detail = render(
      <ItemDetails
        id="stone"
        memory={oldMemory}
        follow={vi.fn()}
        onAuthLost={authLost}
        onDirtyChange={vi.fn()}
      />,
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Remove photograph' }),
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Confirm removal', hidden: true }),
    );
    expect(removeItemPhoto).toHaveBeenCalledOnce();
    // WHEN navigation unmounts details and restores collection before the command finishes.
    detail.unmount();
    const memory = outcome === 'new identity' ? traversal() : oldMemory;
    render(
      <Collection memory={memory} follow={vi.fn()} onAuthLost={authLost} />,
    );
    await screen.findByRole('img', { name: 'Photograph of Stone' });
    await act(async () => {
      if (outcome === 'failure') reject(new ApiError(401));
      else finish();
    });
    // THEN only a successful command for this memory owner updates the visible summary.
    if (outcome === 'success') {
      expect(
        screen.queryByRole('img', { name: 'Photograph of Stone' }),
      ).not.toBeInTheDocument();
      expect(memory.snapshot?.page?.items[0].photo).toBeNull();
    } else {
      expect(
        screen.getByRole('img', { name: 'Photograph of Stone' }),
      ).toBeVisible();
      expect(memory.snapshot?.page?.items[0].photo).toEqual(item.photo);
    }
    // AND the traversal is preserved without a stale reload or authentication callback.
    expect(screen.getByRole('searchbox')).toHaveValue('unfinished');
    expect(
      screen.getByRole('button', { name: 'List', pressed: true }),
    ).toBeVisible();
    expect(memory.snapshot?.page?.nextCursor).toBe('later-page');
    expect(getItem).toHaveBeenCalledOnce();
    expect(authLost).not.toHaveBeenCalled();
  },
);
