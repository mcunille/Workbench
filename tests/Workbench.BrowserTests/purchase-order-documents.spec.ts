import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { setAppearance } from './user-menu-fixture';
import { captureEvidence } from './evidence-fixture';

test.setTimeout(120_000);

// A complete one-page PDF with byte-accurate cross references, matching the
// server validator fixture. Only synthetic, non-sensitive content is uploaded.
function invoicePdf(marker: string) {
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>',
  ];
  let output = `%PDF-1.7\n% ${marker}\n`;
  const offsets: number[] = [];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(output, 'ascii'));
    output += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = Buffer.byteLength(output, 'ascii');
  output += `xref\n0 4\n0000000000 65535 f \n`;
  output += offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  output += `trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(output, 'ascii');
}

async function orderedPurchase(page: Page) {
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`PO-06 invoice files ${Date.now()}`);
  await page.getByLabel('Supplier name', { exact: true }).fill('Sample gemstone supplier');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Blue sapphire');
  await page.getByLabel('Quantity 1', { exact: true }).fill('1');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.getByRole('button', { name: 'Record as ordered', exact: true }).click();
  await page.getByLabel('Order date', { exact: true }).fill('2026-09-18');
  await page.getByRole('button', { name: 'Confirm order', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  return `/api/beta${new URL(page.url()).pathname}`;
}

test('multiple invoice files persist, recover a lost acknowledgement, and leave agreement revisions unchanged', async ({ page }) => {
  // GIVEN an ordered purchase and two independent supplier PDF files.
  const orderApi = await orderedPurchase(page);
  const before = await (await page.request.get(orderApi)).json();
  const files = page.getByRole('region', { name: 'Invoice files', exact: true });
  const first = invoicePdf('Synthetic supplier invoice');
  const second = invoicePdf('Synthetic supplier packing slip');
  let uploads = 0;
  await page.route(`**${orderApi}/documents`, async route => {
    if (route.request().method() !== 'POST') return route.continue();
    uploads++;
    const response = await route.fetch();
    expect(response.ok()).toBe(true);
    if (uploads === 1) await route.abort('failed');
    else await route.fulfill({ response });
  });

  // WHEN keyboard activation opens a multi-file selection and the first saved
  // upload loses its response, the untouched second file remains in the queue.
  await files.getByRole('button', { name: 'Add invoice files', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(files.getByRole('heading', { name: 'Add invoice files', exact: true })).toBeFocused();
  await files.getByLabel('Choose files').setInputFiles([
    { name: 'invoice.pdf', mimeType: 'application/pdf', buffer: first },
    { name: 'packing-slip.pdf', mimeType: 'application/pdf', buffer: second },
  ]);
  await files.getByLabel('File label 1', { exact: true }).fill('Supplier invoice');
  await files.getByLabel('File label 2', { exact: true }).fill('Packing slip');
  await files.getByRole('button', { name: 'Upload files', exact: true }).click();
  await expect(files.getByRole('button', { name: 'Check and retry file', exact: true })).toBeVisible();
  expect(uploads).toBe(1);
  await files.getByRole('button', { name: 'Check and retry file', exact: true }).click();
  await files.getByRole('button', { name: 'Upload remaining files', exact: true }).click();
  await files.getByRole('button', { name: 'Done', exact: true }).click();

  // THEN operation lookup resolves the first upload without resending it and
  // both files download as the original PDF bytes after a fresh page load.
  expect(uploads).toBe(2);
  await expect(files.getByRole('button', { name: 'Add invoice files', exact: true })).toBeFocused();
  await page.reload();
  await expect(files.locator('.po-document-list > li')).toHaveCount(2);
  for (const [label, expected] of [['Supplier invoice', first], ['Packing slip', second]] as const) {
    const downloaded = page.waitForEvent('download');
    await files.getByRole('button', { name: `Download ${label}`, exact: true }).click();
    const download = await downloaded;
    expect(download.suggestedFilename()).toMatch(/^document-[a-f0-9]{32}\.pdf$/);
    const stream = await download.createReadStream();
    expect(stream).not.toBeNull();
    const chunks: Buffer[] = [];
    for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
    expect(Buffer.concat(chunks)).toEqual(expected);
  }

  // AND both appearances retain accessible actions at desktop and narrow widths,
  // including enlarged text. Optional screenshots remain outside source control.
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: 960 });
    await page.evaluate(enlarged => { document.documentElement.style.fontSize = enlarged ? '200%' : ''; }, width === 320);
    for (const dark of [false, true]) {
      await setAppearance(page, dark);
      await files.scrollIntoViewIfNeeded();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await expect(files.getByRole('button', { name: 'Download Supplier invoice', exact: true })).toBeVisible();
      await captureEvidence(files, `po-06/files-${width}-${dark ? 'dark' : 'light'}.png`);
    }
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await page.setViewportSize({ width: 1440, height: 960 });

  // WHEN renaming one file and cancelling removal of the other, only the label
  // changes. Explicit confirmation then revokes the removed file's download.
  await files.getByRole('button', { name: 'Rename Supplier invoice', exact: true }).click();
  await files.getByLabel('File label', { exact: true }).fill('Final supplier invoice');
  await files.getByRole('button', { name: 'Save label', exact: true }).click();
  await expect(files.getByRole('button', { name: 'Download Final supplier invoice', exact: true })).toBeVisible();
  const listBeforeRemoval = await (await page.request.get(`${orderApi}/documents`)).json();
  const removed = listBeforeRemoval.documents.find((file: { label: string }) => file.label === 'Packing slip');
  await files.getByRole('button', { name: 'Remove Packing slip', exact: true }).click();
  await files.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(files.getByRole('button', { name: 'Download Packing slip', exact: true })).toBeVisible();
  await files.getByRole('button', { name: 'Remove Packing slip', exact: true }).click();
  await files.getByRole('button', { name: 'Confirm removal', exact: true }).click();
  await expect(files.locator('.po-document-list > li')).toHaveCount(1);
  expect((await page.request.get(`${orderApi}/documents/${removed.id}/download`)).status()).toBe(404);
  const after = await (await page.request.get(orderApi)).json();
  expect(after.revision).toBe(before.revision);
  expect(after.draft).toEqual(before.draft);
  expect(after.version).not.toBe(before.version);

  // THEN an amendment started without reloading uses the refreshed order version.
  await page.getByRole('button', { name: 'Create amendment', exact: true }).click();
  await page.locator('.po-line-disclosure > summary').first().click();
  await page.getByLabel('Quantity 1', { exact: true }).fill('2');
  await page.getByLabel('Amendment reason', { exact: true }).fill('Supplier confirmed a matched pair.');
  await page.getByRole('button', { name: 'Review amendment', exact: true }).click();
  await page.getByRole('button', { name: 'Record amendment', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  await page.reload();
  await expect(files.getByRole('button', { name: 'Download Final supplier invoice', exact: true })).toBeVisible();
  await expect(files.locator('.po-document-list > li')).toHaveCount(1);
  expect(Number((await (await page.request.get(orderApi)).json()).revision)).toBe(Number(before.revision) + 1);
});
