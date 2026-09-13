import type { DraftPage } from '../../api/purchaseOrders';
export class DraftMemory {
  page?: DraftPage;
  scrollY = 0;
  query = '';
  save(page: DraftPage, query = this.query) { this.page = page; this.query = query; }
  savePosition(top: number) { this.scrollY = top; }
  invalidate() { this.page = undefined; this.scrollY = 0; }
}
