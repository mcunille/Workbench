import type { DraftPage } from '../../api/purchaseOrders';
export class DraftMemory {
  page?: DraftPage;
  scrollY = 0;
  query = '';
  state = '';
  save(page: DraftPage, query = this.query, state = this.state) { this.page = page; this.query = query; this.state = state; }
  savePosition(top: number) { this.scrollY = top; }
  invalidate() { this.page = undefined; this.scrollY = 0; }
}
