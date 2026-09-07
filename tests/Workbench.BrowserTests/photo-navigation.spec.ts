import { expect, test } from '@playwright/test';
import { cameraImage, photoSignIn, savedPhotoItem } from './photo-fixture';

test('a removal finishing after confirmed navigation updates the restored collection', async ({ page }) => {
  test.setTimeout(120_000);
  // GIVEN a real photographed item already loaded in the collection's private memory.
  await photoSignIn(page);
  const name = `Pending photo ${crypto.randomUUID()}`;
  await savedPhotoItem(page, name);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByText('Photograph updated.', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  const photograph = page.getByAltText(`Photograph of ${name}`, { exact: true });
  await expect(photograph).toBeVisible();
  await page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) }).click();
  let release!: () => void;
  let entered!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  const requestEntered = new Promise<void>(resolve => { entered = resolve; });
  await page.route('**/api/items/*/photo', async route => {
    if (route.request().method() === 'DELETE') {
      entered();
      await pending;
    }
    await route.continue();
  });
  // WHEN removal is pending and the collector explicitly confirms leaving details.
  await page.getByRole('button', { name: 'Remove photograph', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm removal', exact: true }).click();
  await requestEntered;
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await page.getByRole('button', { name: 'Discard changes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
  await expect(photograph).toBeVisible();
  const completed = page.waitForResponse(response => response.request().method() === 'DELETE' && new URL(response.url()).pathname.endsWith('/photo'));
  release();
  expect((await completed).ok()).toBe(true);
  // THEN the mounted collection stops advertising the removed photo without a new navigation.
  await expect(photograph).toHaveCount(0);
  await expect(page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) })).toBeVisible();
});
