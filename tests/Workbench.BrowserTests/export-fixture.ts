import { expect, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';

// A Playwright page belongs to one test; only IDs created by this helper are cleanup-owned.
const ownedExportItems = new WeakMap<Page, Set<string>>();

export async function createExportItem(page: Page, name: string, notes = 'First line\nQuoted "detail", café', location = 'Tray A') {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name, notes, location },
  });
  expect(response.status()).toBe(201);
  const item = await response.json();
  const owned = ownedExportItems.get(page) ?? new Set<string>();
  owned.add(item.id);
  ownedExportItems.set(page, owned);
  return item;
}


export async function archiveExportItems(page: Page) {
  const ids = ownedExportItems.get(page);
  if (!ids?.size) return;
  const failures: unknown[] = [];
  try {
    const csrfResponse = await page.request.get('/api/auth/antiforgery');
    expect(csrfResponse.status()).toBe(200);
    const csrf = await csrfResponse.json();
    for (const id of ids) {
      try {
        // Read the saved version because the test may have edited or archived its own record.
        const detail = await page.request.get(`/api/items/${id}`);
        expect(detail.status()).toBe(200);
        const item = await detail.json();
        if (item.archivedAtUtc !== null) continue;
        const response = await page.request.post(`/api/items/${id}/archive`, {
          headers: { 'X-CSRF-TOKEN': csrf.requestToken },
          data: { expectedVersion: item.version },
        });
        expect(response.status()).toBe(200);
        expect((await response.json()).archivedAtUtc).not.toBeNull();
      } catch (error) {
        // Finish other owned records even if one cleanup fails, then report every failure.
        failures.push(error);
      }
    }
  } finally {
    ownedExportItems.delete(page);
  }
  if (failures.length) throw new AggregateError(failures, 'Could not archive all H7-owned test records.');
}
// Parse quoted RFC 4180 fields independently of the server encoder, including embedded CR/LF.
export function parseExportCsv(bytes: Buffer) {
  expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
  const text = bytes.toString('utf8').slice(1);
  const rows: string[][] = []; let row: string[] = []; let field = ''; let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char === '"') {
      if (quoted && text[i + 1] === '"') { field += '"'; i++; } else quoted = !quoted;
    } else if (!quoted && char === ',') { row.push(field); field = ''; }
    else if (!quoted && char === '\r' && text[i + 1] === '\n') { row.push(field); rows.push(row); row = []; field = ''; i++; }
    else field += char;
  }
  expect(quoted).toBe(false); expect(field).toBe(''); expect(row).toEqual([]);
  expect(rows.shift()).toEqual(['schema_version', 'exported_at_utc', 'scope', 'item_id', 'tracking_kind', 'name', 'notes', 'location', 'is_archived', 'created_at_utc', 'archived_at_utc', 'acquisition_id', 'acquisition_method', 'acquisition_source', 'acquisition_date_precision', 'acquisition_year', 'acquisition_month', 'acquisition_day', 'acquisition_notes']);
  for (const record of rows) {
    expect(record).toHaveLength(19);
    expect(record[0]).toBe('2');
  }
  return rows;
}

export async function downloadExport(page: Page) {
  const event = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Download CSV', exact: true }).click();
  const download = await event;
  expect(download.suggestedFilename()).toMatch(/^workbench-records-v2-(active|all)-.+\.csv$/);
  expect(await download.failure()).toBeNull();
  const path = await download.path(); expect(path).not.toBeNull();
  return parseExportCsv(await readFile(path!));
}
