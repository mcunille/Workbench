import { expect, test } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { photoSignIn } from '../photo-fixture';
const output = fileURLToPath(new URL('../../../artifacts/h4-video/', import.meta.url));
const scenes = JSON.parse(await readFile(new URL('./h4-narration.json', import.meta.url), 'utf8')) as { id: string; title: string; text: string }[];

test('record H4 editing and real session conflict recovery', async ({ browser }) => {
  // GIVEN a synthetic item and independently authenticated sessions, off camera.
  await mkdir(output, { recursive: true });
  const durations = JSON.parse(await readFile(`${output}/durations.json`, 'utf8')) as Record<string, number>;
  const login = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const setup = await login.newPage();
  await photoSignIn(setup);
  const csrf = await (await setup.request.get('/api/auth/antiforgery')).json();
  const response = await setup.request.post('/api/items', { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { creationRequestId: crypto.randomUUID(), name: 'Blue stone', notes: 'September fair', location: 'Tray A' } });
  expect(response.status()).toBe(201);
  const item = await response.json();
  const otherContext = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const other = await otherContext.newPage();
  await photoSignIn(other);
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:4179', storageState: await login.storageState(), viewport: { width: 1280, height: 900 }, recordVideo: { dir: `${output}/raw`, size: { width: 1280, height: 900 } } });
  await login.close();
  const start = Date.now();
  const page = await context.newPage();
  const video = page.video()!;
  const timeline: ({ id: string; title: string; text: string; start: number; end: number })[] = [];
  async function scene(id: string, action: () => Promise<void>) {
    const script = scenes.find(scene => scene.id === id)!;
    const begin = Date.now();
    await action();
    // Presentation pacing aligns the actual interaction with synthetic narration.
    await page.waitForTimeout(Math.max(800, durations[id] * 1000 + 1400 - (Date.now() - begin)));
    timeline.push({ ...script, start: (begin - start) / 1000, end: (Date.now() - start) / 1000 });
  }
  await page.goto(`/inventory/${item.id}`);
  await scene('welcome', async () => { await expect(page.getByRole('heading', { name: 'Blue stone', exact: true })).toBeVisible(); });
  await scene('cancel', async () => {
    await page.getByRole('button', { name: 'Edit details', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill('An uncertain identification');
    await page.waitForTimeout(3500);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Blue stone', exact: true })).toBeVisible();
  });
  await scene('mobile', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Edit details', exact: true }).click();
    await page.getByLabel('Storage location (optional)', { exact: true }).fill('Display box');
    await page.route(`**/api/items/${item.id}`, route => route.request().method() === 'PUT' ? route.fulfill({ status: 503, json: {} }) : route.continue(), { times: 1 });
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
    await page.getByRole('button', { name: 'Retry save', exact: true }).scrollIntoViewIfNeeded();
    await page.waitForTimeout(9000);
    await page.getByRole('button', { name: 'Retry save', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await scene('conflict', async () => {
    await other.goto(`/inventory/${item.id}`);
    for (const session of [page, other]) await session.getByRole('button', { name: 'Edit details', exact: true }).click();
    await page.getByLabel('Notes (optional)', { exact: true }).fill('September fair; confirmed sapphire');
    await other.getByLabel('Name', { exact: true }).fill('Blue sapphire');
    await other.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(other.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Review my edits', exact: true })).toBeVisible();
  });
  await scene('review', async () => {
    await page.getByRole('button', { name: 'Review my edits', exact: true }).click();
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Blue sapphire');
    await page.getByLabel('Notes (optional)', { exact: true }).fill('September fair; confirmed sapphire');
    await page.getByLabel('Name', { exact: true }).scrollIntoViewIfNeeded();
    await page.waitForTimeout(8000);
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
  });
  await scene('search', async () => {
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill('confirmed sapphire');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.waitForTimeout(3500);
    await page.getByRole('link').filter({ has: page.getByText('Blue sapphire', { exact: true }) }).click();
    await page.reload();
    await expect(page.getByText('Display box', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Blue sapphire', exact: true })).toBeVisible();
  });
  await otherContext.close();
  await context.close();
  await video.saveAs(`${output}/walkthrough.webm`);
  await writeFile(`${output}/timeline.json`, JSON.stringify(timeline, null, 2));
});
