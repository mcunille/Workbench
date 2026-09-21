/** A display label only: never write a generated label back into a purchase. */
export function purchaseLabel(purchase: {
  title: string | null;
  supplierName: string | null;
  firstItemDescription?: string | null;
  entries?: readonly { description: string | null }[];
}, state = 'Draft'): string {
  return purchase.title?.trim() || purchase.supplierName?.trim()
    || purchase.firstItemDescription?.trim()
    || purchase.entries?.find(entry => entry.description?.trim())?.description?.trim()
    || (state === 'Ordered' ? 'Purchase order' : 'Empty draft');
}
