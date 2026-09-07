import { expect, test } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { cameraImage, photoSignIn, savedPhotoItem } from '../photo-fixture';
const output = fileURLToPath(
  new URL('../../../artifacts/h2-video/', import.meta.url),
);
const scenes = JSON.parse(
  await readFile(new URL('./h2-narration.json', import.meta.url), 'utf8'),
) as { id: string; title: string; text: string }[];

test('record the narrated H2 photograph workflow against the real database', async ({
  browser,
}) => {
  // GIVEN a saved item, authenticated off camera, and synthetic demonstration images.
  await mkdir(output, { recursive: true });
  const durations = JSON.parse(
    await readFile(`${output}/durations.json`, 'utf8'),
  ) as Record<string, number>;
  const login = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const setup = await login.newPage();
  await photoSignIn(setup);
  const url = await savedPhotoItem(setup, 'Blue sapphire');
  const original = await cameraImage(setup);
  const replacement = await cameraImage(setup, 224);
  const context = await browser.newContext({
    baseURL: 'http://127.0.0.1:4179',
    storageState: await login.storageState(),
    viewport: { width: 1280, height: 900 },
    colorScheme: 'light',
    recordVideo: { dir: `${output}/raw`, size: { width: 1280, height: 900 } },
  });
  await login.close();
  const start = Date.now();
  const page = await context.newPage();
  const video = page.video()!;
  const timeline: {
    id: string;
    title: string;
    text: string;
    start: number;
    end: number;
  }[] = [];
  async function scene(id: string, action: () => Promise<void>) {
    const script = scenes.find((scene) => scene.id === id)!;
    const begin = Date.now();
    await action();
    // Deliberate presentation pacing for the recorded narration, not test synchronization.
    await page.waitForTimeout(
      Math.max(800, durations[id] * 1000 + 1400 - (Date.now() - begin)),
    );
    timeline.push({
      ...script,
      start: (begin - start) / 1000,
      end: (Date.now() - start) / 1000,
    });
  }
  await page.goto(url);
  await scene('welcome', async () => {
    await expect(
      page.getByRole('heading', { name: 'Blue sapphire', exact: true }),
    ).toBeVisible();
  });
  await scene('prepare', async () => {
    // WHEN choosing THEN show local preparation before explicit upload.
    await page
      .getByLabel('Choose photograph', { exact: true })
      .setInputFiles(original);
    await expect(
      page.getByAltText('Prepared photograph preview'),
    ).toBeVisible();
    const prepared = await page
      .getByAltText('Prepared photograph preview')
      .evaluate(async (image: HTMLImageElement) => ({
        width: image.naturalWidth,
        height: image.naturalHeight,
        bytes: (await (await fetch(image.src)).blob()).size,
      }));
    await writeFile(
      `${output}/preparation.json`,
      JSON.stringify(
        { sourceBytes: original.buffer.length, ...prepared },
        null,
        2,
      ),
    );
    await page
      .getByAltText('Prepared photograph preview')
      .scrollIntoViewIfNeeded();
    await page.waitForTimeout(7000);
    await page
      .getByRole('button', { name: 'Upload photograph', exact: true })
      .click();
    await expect(
      page.getByText('Current saved photograph loaded.', { exact: true }),
    ).toBeVisible();
    await page
      .getByRole('heading', { name: 'Blue sapphire', exact: true })
      .scrollIntoViewIfNeeded();
  });
  await scene('persist', async () => {
    await page.reload();
    await expect(
      page.getByAltText('Photograph of Blue sapphire'),
    ).toBeVisible();
    await page
      .getByRole('link', { name: 'Back to collection', exact: true })
      .click();
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Grid', exact: true }).click();
    const second = await browser.newContext({
      baseURL: 'http://127.0.0.1:4179',
    });
    try {
      const other = await second.newPage();
      await photoSignIn(other);
      await other.goto(url);
      await expect(
        other.getByAltText('Photograph of Blue sapphire'),
      ).toBeVisible();
    } finally {
      await second.close();
    }
  });
  await scene('replace', async () => {
    await page
      .getByRole('link')
      .filter({ has: page.getByText('Blue sapphire', { exact: true }) })
      .click();
    let requests = 0;
    await page.route('**/api/items/*/photo', async (route) => {
      if (route.request().method() !== 'PUT') return route.continue();
      if (++requests === 1) {
        expect((await route.fetch()).ok()).toBe(true);
        await route.abort('failed');
      } else await route.continue();
    });
    await page
      .getByLabel('Choose photograph', { exact: true })
      .setInputFiles(replacement);
    await expect(
      page.getByAltText('Prepared photograph preview'),
    ).toBeVisible();
    await page
      .getByAltText('Prepared photograph preview')
      .scrollIntoViewIfNeeded();
    await page.waitForTimeout(3000);
    await page
      .getByRole('button', { name: 'Upload photograph', exact: true })
      .click();
    await expect(
      page.getByRole('button', { name: 'Retry upload', exact: true }),
    ).toBeVisible();
    await page.waitForTimeout(5000);
    await page
      .getByRole('button', { name: 'Retry upload', exact: true })
      .click();
    await expect(
      page.getByText('Current saved photograph loaded.', { exact: true }),
    ).toBeVisible();
    await page.unroute('**/api/items/*/photo');
  });
  await scene('mobile', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page
      .getByRole('heading', { name: 'Blue sapphire', exact: true })
      .scrollIntoViewIfNeeded();
    await page
      .getByRole('combobox', { name: 'Appearance', exact: true })
      .selectOption('dark');
    await page.waitForTimeout(3000);
    await page
      .getByRole('combobox', { name: 'Appearance', exact: true })
      .selectOption('light');
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  });
  await scene('remove', async () => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page
      .getByRole('button', { name: 'Remove photograph', exact: true })
      .click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.waitForTimeout(4000);
    await page
      .getByRole('button', { name: 'Confirm removal', exact: true })
      .click();
    await expect(
      page.getByRole('button', { name: 'Remove photograph', exact: true }),
    ).toHaveCount(0);
    await page
      .getByRole('heading', { name: 'Blue sapphire', exact: true })
      .scrollIntoViewIfNeeded();
    await expect(
      page.getByText('Studio tray A', { exact: true }),
    ).toBeVisible();
  });
  await context.close();
  await video.saveAs(`${output}/walkthrough.webm`);
  await writeFile(`${output}/timeline.json`, JSON.stringify(timeline, null, 2));
});
