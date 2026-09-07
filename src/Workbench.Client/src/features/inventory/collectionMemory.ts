import type { ItemDetail, ItemPage } from '../../api/items';
type Snapshot = {
  view: 'grid' | 'list';
  draft: string;
  query: string;
  page?: ItemPage;
};

// One private traversal lives only as long as its authenticated application owner.
export class CollectionMemory {
  private readonly invalidationListeners = new Set<() => void>();
  private readonly photoListeners = new Set<
    (id: string, photo: ItemDetail['photo']) => void
  >();
  snapshot?: Snapshot;
  selectedId?: string;
  scrollY = 0;
  save(snapshot: Snapshot) {
    this.snapshot = snapshot;
  }
  select(id: string | undefined) {
    this.selectedId = id;
  }
  savePosition(top: number) {
    this.scrollY = top;
  }
  invalidate() {
    if (this.snapshot) this.snapshot = { ...this.snapshot, page: undefined };
    this.selectedId = undefined;
    this.scrollY = 0;
    for (const listener of this.invalidationListeners) listener();
  }
  subscribeInvalidation(listener: () => void) {
    this.invalidationListeners.add(listener);
    return () => {
      this.invalidationListeners.delete(listener);
    };
  }
  subscribePhotos(listener: (id: string, photo: ItemDetail['photo']) => void) {
    this.photoListeners.add(listener);
    return () => {
      this.photoListeners.delete(listener);
    };
  }
  removeUnavailable(id: string) {
    const page = this.snapshot?.page;
    if (this.snapshot && page)
      this.snapshot = {
        ...this.snapshot,
        page: { ...page, items: page.items.filter((item) => item.id !== id) },
      };
  }
  updatePhoto(id: string, photo: ItemDetail['photo']) {
    const page = this.snapshot?.page;
    if (this.snapshot && page)
      this.snapshot = {
        ...this.snapshot,
        page: {
          ...page,
          items: page.items.map((item) =>
            item.id === id ? { ...item, photo } : item,
          ),
        },
      };
    for (const listener of this.photoListeners) listener(id, photo);
  }
}
