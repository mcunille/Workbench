import type { GemFilters, GemReferencePageResponse } from '../../api/gemReference';

export type LibraryFilters = Omit<GemFilters, 'cursor'>;
type Snapshot = { draft: LibraryFilters; filters: LibraryFilters; page?: GemReferencePageResponse };

// Private traversal state belongs to one mounted authenticated application.
export class GemLibraryMemory {
  snapshot?: Snapshot;
  selected?: string;
  scrollY = 0;
  save(snapshot: Snapshot) { this.snapshot = snapshot; }
  select(href?: string) { this.selected = href; }
  savePosition(top: number) { this.scrollY = top; }
  invalidateResults() { if (this.snapshot) this.snapshot = { draft: this.snapshot.draft, filters: this.snapshot.filters }; this.selected = undefined; this.scrollY = 0; }
  clear() { this.snapshot = undefined; this.selected = undefined; this.scrollY = 0; }
}
