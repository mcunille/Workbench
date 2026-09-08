import { expect, type Page } from '@playwright/test';
export async function createArchived(page: Page, name = `H6 ${crypto.randomUUID()}`) {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const created = await page.request.post('/api/items', { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { creationRequestId: crypto.randomUUID(), name, notes: 'Retained collection notes', location: 'Tray A' } });
  expect(created.status()).toBe(201);
  const item = await created.json();
  return lifecycle(page, item.id, 'archive', item.version);
}
export async function lifecycle(page: Page, id: string, action: 'archive' | 'restore', expectedVersion: string) {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post(`/api/items/${id}/${action}`, { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { expectedVersion } });
  expect(response.status()).toBe(200);
  return response.json();
}
export const restore = (page: Page) => page.getByRole('button', { name: 'Restore to collection', exact: true });
export const confirmRestore = (page: Page) => page.getByRole('button', { name: 'Confirm restore', exact: true });
export async function searchArchive(page: Page, query: string) {
  await page.getByRole('searchbox', { name: 'Search archive', exact: true }).fill(query);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
}
