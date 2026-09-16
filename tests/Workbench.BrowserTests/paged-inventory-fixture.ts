import { expect, type Page } from '@playwright/test';
import type { ItemDetail } from '../../src/Workbench.Client/src/api/items';

export function syntheticItem(index: number, name: string, archived = false): ItemDetail {
  return {
    id: '00000000-0000-4000-8000-' + String(index + 1).padStart(12, '0'),
    name, notes: null, location: 'Tray H3', photo: null,
    createdAtUtc: '2026-09-01T12:00:00Z', version: 'AAAAAAAAB9E=',
    archivedAtUtc: archived ? '2026-09-02T12:00:00Z' : null,
  };
}

// These synthetic responses own UI traversal only, never server search/cursor correctness.
export async function pagedInventory(page: Page, items: ItemDetail[], archived = false) {
  const listPath = archived ? '/api/items/archived' : '/api/items';
  await page.route(archived ? '**/api/items/archived**' : '**/api/items**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    expect(request.method()).toBe('GET');
    if (url.pathname === listPath) {
      const query = (url.searchParams.get('q') ?? '').toLowerCase();
      const matching = items.filter(item => item.name.toLowerCase().includes(query));
      const cursor = url.searchParams.get('cursor');
      expect(cursor === null || cursor === 'page-2').toBe(true);
      const offset = cursor === null ? 0 : 50;
      return route.fulfill({ json: { items: matching.slice(offset, offset + 50), nextCursor: matching.length > offset + 50 ? 'page-2' : null } });
    }
    const item = items.find(item => url.pathname === '/api/items/' + item.id || url.pathname === '/api/items/' + item.id + '/acquisition');
    if (item) return route.fulfill({ json: url.pathname.endsWith('/acquisition') ? { acquisition: null, itemVersion: item.version } : item });
    await route.fulfill({ status: 501, json: { title: 'Undeclared synthetic inventory request' } });
    expect(url.pathname, 'Synthetic inventory tests must declare every inventory request').toBe(listPath);
  });
}
