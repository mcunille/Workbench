import { expect, type Page } from '@playwright/test';

export async function createPiece(page: Page, name: string) {
  const csrf = await (await page.request.get('/api/beta/auth/antiforgery')).json();
  const response = await page.request.post('/api/beta/items', {
    headers: { 'X-Workbench-Api-Revision': 'beta-2', 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name, location: 'Tray A', notes: 'Synthetic acquisition verification record' },
  });
  expect(response.status()).toBe(201);
  return response.json() as Promise<{ id: string; name: string; version: string }>;
}

export async function createOrigin(page: Page, itemId: string, source: string) {
  const csrf = await (await page.request.get('/api/beta/auth/antiforgery')).json();
  const item = await (await page.request.get(`/api/beta/items/${itemId}`)).json();
  const response = await page.request.post(`/api/beta/items/${itemId}/acquisition`, {
    headers: { 'X-Workbench-Api-Revision': 'beta-2', 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), expectedItemVersion: item.version, method: 'Purchase', source, year: 2020, notes: 'Three individually recorded stones acquired together.' },
  });
  expect(response.status()).toBe(201);
  return response.json();
}

export const acquisitionPanel = (page: Page) => page.getByRole('region', { name: 'Acquisition', exact: true });
