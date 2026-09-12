import type { DraftPage } from '../../api/purchaseOrders';
export class DraftMemory {
  page?: DraftPage;
  scrollY = 0;
  save(page: DraftPage) { this.page = page; }
  savePosition(top: number) { this.scrollY = top; }
  invalidate() { this.page = undefined; this.scrollY = 0; }
}
