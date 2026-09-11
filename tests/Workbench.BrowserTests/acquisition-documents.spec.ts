import { expect, test } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';
import { createOrigin, createPiece } from './shared-acquisition-fixture';
import { receiptImage } from './acquisition-document-fixture';
import { setAppearance } from './user-menu-fixture';

test('H11 retained paperwork survives response loss and remains usable at 320px', async ({ page }) => {
  // GIVEN a saved acquisition and a labeled document draft.
  await useAuthenticatedSession(page);
  const piece = await createPiece(page, `Paperwork ${crypto.randomUUID()}`);
  const context = await createOrigin(page, piece.id, 'Autumn mineral fair');
  const path = `/api/items/${piece.id}/acquisition/${context.acquisition.id}/documents`;
  await page.goto(`/inventory/${piece.id}`);
  await page.getByRole('button', { name: 'Add document', exact: true }).click();
  await page.getByLabel('Document label', { exact: true }).fill('Fair receipt');
  await page.getByLabel('Choose document', { exact: true }).setInputFiles(await receiptImage(page));

  // WHEN the server commits but the browser loses the response, retry resolves the same request.
  await page.route(`**${path}`, async route => {
    if (route.request().method() !== 'POST') { await route.continue(); return; }
    expect((await route.fetch()).status()).toBe(200);
    await route.abort('failed');
  }, { times: 1 });
  await page.getByRole('button', { name: 'Upload document', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Check and retry', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Check and retry', exact: true }).click();

  // THEN one saved document is listed with an accessible download and truthful file information.
  await expect(page.getByRole('button', { name: 'Download Fair receipt', exact: true })).toBeVisible();
  expect((await (await page.request.get(path)).json()).documents).toHaveLength(1);
  const downloaded = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download Fair receipt', exact: true }).click();
  expect((await downloaded).suggestedFilename()).toMatch(/\.png$/);

  // WHEN revisiting at a narrow width and correcting the label THEN layout and saved state remain usable.
  await page.setViewportSize({ width: 320, height: 900 });
  await setAppearance(page, true);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.reload();
  await page.getByRole('button', { name: 'Rename Fair receipt', exact: true }).click();
  await page.getByLabel('Document label', { exact: true }).fill('Fair supporting record');
  await page.getByRole('button', { name: 'Save label', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Download Fair supporting record', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Remove Fair supporting record', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm removal', exact: true }).click();
  await expect(page.getByText('No documents yet.', { exact: true })).toBeVisible();
});
