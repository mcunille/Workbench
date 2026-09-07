import { expect, test } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { photoSignIn, cameraImage } from '../photo-fixture';
const output = fileURLToPath(new URL('../../../artifacts/h3-video/', import.meta.url));
const scenes = JSON.parse(await readFile(new URL('./h3-narration.json', import.meta.url), 'utf8')) as { id: string; title: string; text: string }[];

test('record H3 search and return against the real database', async ({ browser }) => {
  // GIVEN a synthetic collection, authenticated off camera.
  await mkdir(output, { recursive: true });
  const durations = JSON.parse(await readFile(`${output}/durations.json`, 'utf8')) as Record<string, number>;
  const login = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const setup = await login.newPage();
  await photoSignIn(setup);
  let sapphire = '';
  for (const [name, notes, location] of [
    ['Blue sapphire', 'Found at the September fair', 'Tray A, slot 3'],
    ['Silver leaf pendant', 'A gift from a friend', 'Display box'],
    ['Rose-cut garnet', 'Warm red facets', 'Tray B, slot 2'],
  ]) {
    const csrf = await (await setup.request.get('/api/auth/antiforgery')).json();
    const response = await setup.request.post('/api/items', { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { creationRequestId: crypto.randomUUID(), name, notes, location } });
    expect(response.status()).toBe(201);
    if (name === 'Blue sapphire') sapphire = (await response.json()).id;
  }
  await setup.goto(`/inventory/${sapphire}`);
  await setup.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(setup));
  await setup.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(setup.getByText('Photograph updated.', { exact: true })).toBeVisible();
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
    // Presentation pacing aligns the recorded actions with generated narration.
    await page.waitForTimeout(Math.max(800, durations[id] * 1000 + 1400 - (Date.now() - begin)));
    timeline.push({ ...script, start: (begin - start) / 1000, end: (Date.now() - start) / 1000 });
  }
  await page.goto('/inventory');
  const search = page.getByRole('searchbox', { name: 'Search collection', exact: true });
  const submit = page.getByRole('button', { name: 'Search', exact: true });
  const link = page.getByRole('link').filter({ has: page.getByText('Blue sapphire', { exact: true }) });
  await scene('welcome', async () => { await expect(link).toBeVisible(); });
  await scene('search', async () => {
    await search.fill('SEPTEMBER FAIR');
    await submit.click();
    await expect(page.locator('.collection-list > li')).toHaveCount(1);
  });
  await scene('mobile', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await link.click();
    await expect(page.getByText('Tray A, slot 3', { exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(link).toBeFocused();
    await expect(search).toHaveValue('SEPTEMBER FAIR');
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
    await search.scrollIntoViewIfNeeded();
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await scene('retry', async () => {
    await page.route('**/api/items?*', route => route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{}' }), { times: 1 });
    await search.fill('Tray A');
    await submit.click();
    await expect(page.getByRole('alert')).toBeVisible();
    await page.waitForTimeout(2500);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(link).toBeVisible();
  });
  await scene('clear', async () => {
    await search.fill('No such piece');
    await submit.click();
    await expect(page.getByText(/no matches/i)).toBeVisible();
    await page.waitForTimeout(2000);
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(page.locator('.collection-list > li')).toHaveCount(3);
  });
  await context.close();
  await video.saveAs(`${output}/walkthrough.webm`);
  await writeFile(`${output}/timeline.json`, JSON.stringify(timeline, null, 2));
});
