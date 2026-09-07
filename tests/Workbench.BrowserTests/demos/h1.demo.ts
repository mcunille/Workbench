import { expect, test, type Page } from '@playwright/test';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const output = fileURLToPath(new URL('../../../artifacts/h1-video/', import.meta.url));
const scenes = JSON.parse(await readFile(new URL('./h1-narration.json', import.meta.url), 'utf8')) as { id: string; title: string; text: string }[];

async function signIn(page: Page) {
  await page.goto('/');
  await page.getByLabel('Email', { exact: true }).fill('browser-admin@example.test');
  await page.getByLabel('Password', { exact: true }).fill('Browser Correct Horse 9!');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
}

test('record the narrated H1 scenario against the real database', async ({ browser }) => {
  // GIVEN a fresh disposable database and an authenticated collector. Authenticate off camera.
  await mkdir(output, { recursive: true });
  const durations = JSON.parse(await readFile(`${output}/durations.json`, 'utf8')) as Record<string, number>;
  const login = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const loginPage = await login.newPage();
  // Shared styling also reaches public and account surfaces; inspect these off camera.
  await loginPage.setViewportSize({ width: 320, height: 900 });
  await loginPage.goto('/');
  await expect(loginPage.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  expect(await loginPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await loginPage.screenshot({ path: `${output}/sign-in-320.png`, fullPage: true });
  await signIn(loginPage);
  for (const route of ['account', 'administration']) {
    await loginPage.goto(`/${route}`);
    await expect(loginPage.getByRole('heading', { name: route === 'account' ? 'Account' : 'Administration', exact: true })).toBeVisible();
    await expect(loginPage.getByText(route === 'account' ? 'Loading sessions…' : 'Loading tenant users…', { exact: true })).toBeHidden();
    expect(await loginPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await loginPage.screenshot({ path: `${output}/${route}-320.png`, fullPage: true });
  }
  const context = await browser.newContext({
    baseURL: 'http://127.0.0.1:4179', storageState: await login.storageState(),
    viewport: { width: 1280, height: 900 }, colorScheme: 'light',
    recordVideo: { dir: `${output}/raw`, size: { width: 1280, height: 900 } },
  });
  await login.close();
  const start = Date.now();
  const page = await context.newPage();
  const video = page.video()!;
  const timeline: { id: string; title: string; text: string; start: number; end: number }[] = [];
  async function scene(id: string, action: () => Promise<void>) {
    const script = scenes.find(scene => scene.id === id)!;
    const begin = Date.now();
    await action();
    // Deliberate presentation pacing: leave each scene visible for its narration.
    await page.waitForTimeout(Math.max(800, durations[id] * 1000 + 1400 - (Date.now() - begin)));
    timeline.push({ ...script, start: (begin - start) / 1000, end: (Date.now() - start) / 1000 });
  }
  const item = (name: string) => page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) });
  await page.goto('/inventory');
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
  await scene('welcome', async () => { await expect(page.getByRole('link', { name: 'Add item', exact: true })).toBeVisible(); });
  await scene('create', async () => {
    // WHEN a gemstone is entered and saved through the application.
    await page.getByRole('link', { name: 'Add item', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).pressSequentially('Blue sapphire', { delay: 75 });
    await page.getByLabel('Notes (optional)', { exact: true }).pressSequentially('Cornflower blue in daylight.\nKeep with the certificate from the autumn gem show.', { delay: 30 });
    await page.getByLabel('Storage location (optional)', { exact: true }).pressSequentially('Tray A, slot 3', { delay: 75 });
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Save item', exact: true }).click();
    // THEN the saved detail is visible.
    await expect(page.getByRole('heading', { name: 'Blue sapphire', exact: true })).toBeVisible();
  });
  await scene('reopen', async () => {
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await page.waitForTimeout(2000);
    await item('Blue sapphire').click();
    await page.reload();
    await expect(page.getByText('Tray A, slot 3', { exact: true })).toBeVisible();
    const second = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
    try {
      const other = await second.newPage();
      await signIn(other);
      await other.goto(page.url());
      await expect(other.getByText('Tray A, slot 3', { exact: true })).toBeVisible();
    } finally { await second.close(); }
  });
  await scene('draft', async () => {
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await page.getByRole('link', { name: 'Add item', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).pressSequentially('A piece to identify later', { delay: 55 });
    await page.getByRole('link', { name: 'Account', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.waitForTimeout(3000);
    await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('A piece to identify later');
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Discard changes', exact: true }).click();
    await expect(item('A piece to identify later')).toHaveCount(0);
  });
  await scene('retry', async () => {
    // GIVEN a real commit whose response is deliberately lost, not a mocked successful save.
    let attempts = 0;
    await page.route('**/api/items', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      if (++attempts === 1) {
        expect((await route.fetch()).status()).toBe(201);
        await route.abort('failed');
      } else await route.continue();
    });
    await page.getByRole('link', { name: 'Add item', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).pressSequentially('Silver pendant', { delay: 65 });
    await page.getByLabel('Storage location (optional)', { exact: true }).fill('Jewelry box, upper drawer');
    await page.getByRole('button', { name: 'Save item', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeVisible();
    await page.waitForTimeout(6500);
    const response = page.waitForResponse(r => r.url().endsWith('/api/items') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Retry save', exact: true }).click();
    expect((await response).status()).toBe(200);
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(item('Silver pendant')).toHaveCount(1);
    await page.unroute('**/api/items');
  });
  await scene('appearance', async () => {
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Grid', exact: true }).click();
    await page.waitForTimeout(1000);
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('light');
  });
  await scene('mobile', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(2500);
    await item('Blue sapphire').click();
    await expect(page.getByText('Tray A, slot 3', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
  await scene('scope', async () => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(item('Blue sapphire')).toHaveCount(1);
    await expect(item('Silver pendant')).toHaveCount(1);
  });
  await context.close();
  await video.saveAs(`${output}/walkthrough.webm`);
  await writeFile(`${output}/timeline.json`, JSON.stringify(timeline, null, 2));
});
