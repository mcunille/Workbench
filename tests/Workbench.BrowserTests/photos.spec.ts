import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { cameraImage, photoSignIn, savedPhotoItem } from './photo-fixture';
const output = fileURLToPath(new URL('../../artifacts/h2/', import.meta.url));
test.setTimeout(180_000);

test('prepares a camera image locally and persists uncropped photos across sessions and views', async ({
  page,
  browser,
}) => {
  // GIVEN a real saved item and a camera-sized synthetic photograph.
  await photoSignIn(page);
  const name = `Photo sapphire ${crypto.randomUUID()}`;
  const url = await savedPhotoItem(page, name);
  const file = await cameraImage(page);
  const uploads: number[] = [];
  await page.route('**/api/items/*/photo', async (route) => {
    if (route.request().method() === 'PUT')
      uploads.push(route.request().postDataBuffer()!.length);
    await route.continue();
  });
  // WHEN selecting THEN only a local preview is produced until explicit upload.
  await page
    .getByLabel('Choose photograph', { exact: true })
    .setInputFiles(file);
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  expect(uploads).toHaveLength(0);
  const preview = await page
    .getByAltText('Prepared photograph preview')
    .evaluate((image: HTMLImageElement) => ({
      width: image.naturalWidth,
      height: image.naturalHeight,
    }));
  expect(preview).toEqual({ width: 2048, height: 1536 });
  await page
    .getByRole('button', { name: 'Upload photograph', exact: true })
    .focus();
  await page.keyboard.press('Enter');
  await expect(
    page.getByText('Photograph updated.', { exact: true }),
  ).toBeVisible();
  expect(uploads).toHaveLength(1);
  expect(uploads[0]).toBeLessThan(file.buffer.length / 2);
  expect(uploads[0]).toBeLessThan(4 * 1024 * 1024 + 4096);
  const id = url.split('/').at(-1)!;
  const detail = await (await page.request.get(`/api/items/${id}`)).json();
  expect(detail.photo.width).toBe(2048);
  expect(detail.photo.height).toBe(1536);
  await page.reload();
  await expect(page.getByAltText(`Photograph of ${name}`)).toBeVisible();
  // THEN another authenticated cookie jar sees the same persisted image.
  const second = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  try {
    const other = await second.newPage();
    await photoSignIn(other);
    await other.goto(url);
    await expect(other.getByAltText(`Photograph of ${name}`)).toBeVisible();
  } finally {
    await second.close();
  }
  await mkdir(output, { recursive: true });
  // AND keyboard controls, both themes and a 320px phone preserve uncropped images.
  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark']) {
      await page
        .getByRole('combobox', { name: 'Appearance', exact: true })
        .selectOption(theme);
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBe(true);
      await page.screenshot({
        path: `${output}/details-${width}-${theme}.png`,
        fullPage: true,
      });
      await page
        .getByRole('link', { name: 'Back to collection', exact: true })
        .click();
      for (const view of ['List', 'Grid']) {
        await page.getByRole('button', { name: view, exact: true }).focus();
        await page.keyboard.press('Enter');
        const image = page.getByAltText(`Photograph of ${name}`);
        await expect(image).toBeVisible();
        expect(
          await image.evaluate((image) => getComputedStyle(image).objectFit),
        ).toBe('contain');
        expect(
          await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
          ),
        ).toBe(true);
        await page.screenshot({
          path: `${output}/${view.toLowerCase()}-${width}-${theme}.png`,
          fullPage: true,
        });
      }
      await page
        .getByRole('link')
        .filter({ has: page.getByText(name, { exact: true }) })
        .click();
    }
  }
});

test('retains exact prepared bytes after a lost response and confirms removal after replacement', async ({
  page,
}) => {
  // GIVEN a real commit whose first response is lost.
  await photoSignIn(page);
  const name = `Retry photograph ${crypto.randomUUID()}`;
  await savedPhotoItem(page, name);
  const commands: {
    requestId: string;
    expectedVersion: string;
    bytes: string;
  }[] = [];
  await page.route('**/api/items/*/photo', async (route) => {
    if (route.request().method() !== 'PUT') return route.continue();
    const request = route.request();
    const form = await new Response(request.postDataBuffer(), {
      headers: { 'Content-Type': request.headers()['content-type'] },
    }).formData();
    commands.push({
      requestId: form.get('requestId') as string,
      expectedVersion: form.get('expectedVersion') as string,
      bytes: Buffer.from(
        await (form.get('file') as File).arrayBuffer(),
      ).toString('base64'),
    });
    if (commands.length === 1) {
      expect((await route.fetch()).ok()).toBe(true);
      await route.abort('failed');
    } else await route.continue();
  });
  await page
    .getByLabel('Choose photograph', { exact: true })
    .setInputFiles(await cameraImage(page));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await page
    .getByRole('button', { name: 'Upload photograph', exact: true })
    .click();
  await page.getByRole('button', { name: 'Retry upload', exact: true }).click();
  await expect(
    page.getByText('Photograph updated.', { exact: true }),
  ).toBeVisible();
  expect(commands).toHaveLength(2);
  expect(commands[0]).toEqual(commands[1]);
  await page.unroute('**/api/items/*/photo');
  const before = await (
    await page.request.get(`/api/items/${page.url().split('/').at(-1)}`)
  ).json();
  // WHEN a replacement is prepared THEN the existing image remains until explicit save.
  await page
    .getByLabel('Choose photograph', { exact: true })
    .setInputFiles(await cameraImage(page, 340));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await expect(page.getByAltText(`Photograph of ${name}`)).toBeVisible();
  await page
    .getByRole('link', { name: 'Back to collection', exact: true })
    .click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await page
    .getByRole('button', { name: 'Upload photograph', exact: true })
    .click();
  await expect(
    page.getByText('Photograph updated.', { exact: true }),
  ).toBeVisible();
  const after = await (
    await page.request.get(`/api/items/${page.url().split('/').at(-1)}`)
  ).json();
  expect(after.photo.id).not.toBe(before.photo.id);
  // THEN removal is confirmed accessibly and preserves the saved item itself.
  await page
    .getByRole('button', { name: 'Remove photograph', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Keep photograph', exact: true }),
  ).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page
    .getByRole('button', { name: 'Remove photograph', exact: true })
    .click();
  await page
    .getByRole('button', { name: 'Confirm removal', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Remove photograph', exact: true }),
  ).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.getByAltText(`Photograph of ${name}`)).toHaveCount(0);
});
