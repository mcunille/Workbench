import { CollectionMemory } from './collectionMemory';

it('notifies only current photo subscribers belonging to this memory owner', () => {
  // GIVEN two authenticated memory owners and a collection listener on the first.
  const first = new CollectionMemory();
  const second = new CollectionMemory();
  const listener = vi.fn();
  const otherListener = vi.fn();
  const unsubscribe = first.subscribePhotos(listener);
  second.subscribePhotos(otherListener);
  // WHEN a photo changes THEN only this owner's current collection receives it.
  first.updatePhoto('stone', null);
  expect(listener).toHaveBeenCalledExactlyOnceWith('stone', null);
  expect(otherListener).not.toHaveBeenCalled();
  // WHEN collection unmounts THEN a later completion no longer notifies that listener.
  unsubscribe();
  first.updatePhoto('stone', null);
  expect(listener).toHaveBeenCalledOnce();
});

it('reconciles a changed photograph while preserving the complete traversal', () => {
  // GIVEN two loaded summaries and private search/navigation context.
  const memory = new CollectionMemory();
  const originalPhoto = {
    id: 'old',
    thumbnailUrl: '/old/thumb',
    detailUrl: '/old/detail',
    width: 100,
    height: 100,
  };
  memory.save({
    view: 'list',
    draft: 'unfinished',
    query: 'stone',
    page: {
      items: [
        {
          id: 'one',
          name: 'Stone',
          location: null,
          photo: originalPhoto,
          createdAtUtc: '',
        },
        {
          id: 'two',
          name: 'Other',
          location: null,
          photo: null,
          createdAtUtc: '',
        },
      ],
      nextCursor: 'next',
    },
  });
  memory.select('one');
  memory.savePosition(600);
  // WHEN mutation succeeds before its detail refresh THEN the old photo is no longer advertised.
  memory.updatePhoto('one', null);
  expect(memory.snapshot?.page?.items[0].photo).toBeNull();
  // AND the authoritative replacement can be reconciled without resetting the traversal.
  const replacement = {
    ...originalPhoto,
    id: 'new',
    thumbnailUrl: '/new/thumb',
  };
  memory.updatePhoto('one', replacement);
  expect(memory.snapshot?.page?.items.map((item) => item.photo)).toEqual([
    replacement,
    null,
  ]);
  expect(memory.snapshot).toMatchObject({
    view: 'list',
    draft: 'unfinished',
    query: 'stone',
    page: { nextCursor: 'next' },
  });
  expect(memory.selectedId).toBe('one');
  expect(memory.scrollY).toBe(600);
});

it('invalidates loaded records after creation while retaining the query and view', () => {
  // GIVEN a loaded traversal that would omit a newly created item.
  const memory = new CollectionMemory();
  memory.save({
    view: 'list',
    query: 'stone',
    draft: 'stone',
    page: { items: [], nextCursor: null },
  });
  // WHEN creation succeeds THEN the next collection mount fetches that traversal anew.
  memory.invalidate();
  expect(memory.snapshot).toEqual({
    view: 'list',
    query: 'stone',
    draft: 'stone',
    page: undefined,
  });
});
