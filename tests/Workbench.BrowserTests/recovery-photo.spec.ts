import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { cameraImage, photoSignIn, savedPhotoItem } from './photo-fixture';

test.setTimeout(180_000);

test('explains a recovery loss on an otherwise usable item', async ({ page }) => {
  // GIVEN a saved item/photo and the API's accepted-loss response (SQL acceptance is integration-tested separately).
  await photoSignIn(page);
  const name = `Recovered photo ${crypto.randomUUID()}`;
  await savedPhotoItem(page, name);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByAltText(`Photograph of ${name}`, { exact: true })).toBeVisible();
  await page.route('**/api/items/*/photo/*/*', route => route.fulfill({
    status: 410,
    contentType: 'application/problem+json',
    body: JSON.stringify({ status: 410, code: 'file_unavailable_after_recovery' }),
  }));
  // WHEN the user opens the item after recovery.
  await page.reload();
  // THEN the persistent notice explains the loss without signing out or suggesting a transient retry.
  await expect(page.getByText('This photograph could not be recovered. Replace it with another copy.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry photograph' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await mkdir('../../artifacts/online-backup', { recursive: true });
  await page.screenshot({ path: '../../artifacts/online-backup/recovered-photo.png', fullPage: true });
});
