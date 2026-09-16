import type { SupplierPage } from '../../api/suppliers';

export class SupplierMemory {
  page?: SupplierPage;
  query = '';
  archived = false;
  scrollY = 0;
  needsRefresh = false;
  save(page: SupplierPage, query: string, archived: boolean) { this.page = page; this.query = query; this.archived = archived; this.needsRefresh = false; }
  savePosition(top: number) { this.scrollY = top; }
  invalidate() { this.needsRefresh = true; }
  clear() { this.page = undefined; this.query = ''; this.archived = false; this.scrollY = 0; this.needsRefresh = false; }
}
