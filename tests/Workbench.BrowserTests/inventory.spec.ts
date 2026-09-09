import { openUserMenu, setAppearance } from './user-menu-fixture';
import { useAuthenticatedSession as signIn, signInThroughUi } from './auth-fixture';
import { browserBaseUrl } from './browser-environment';
import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const screenshotDirectory = fileURLToPath(new URL('../../artifacts/h1/', import.meta.url));

test.setTimeout(120_000);

function itemLink(page: Page, name: string) {
  return page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) });
}

async function inspectLayout(page: Page) {
  const overflow = await page.evaluate(() => [...document.querySelectorAll('body *')]
    .filter(element => element.getBoundingClientRect().right > window.innerWidth + 1 || element.scrollWidth > element.clientWidth + 1)
    .map(element => ({ tag: element.tagName, className: element.className, width: element.getBoundingClientRect().width, scrollWidth: element.scrollWidth })));
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), JSON.stringify(overflow)).toBe(true);
  for (const title of await page.locator('h1, .item-title, .lede, small, dt, label, .workspace-nav a').all()) {
    const titleContrast = await title.evaluate(title => {
      const channels = (value: string) => value.match(/[\d.]+/g)!.slice(0, 3).map(Number);
      const luminance = (color: string) => channels(color).map(channel => {
        const value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
      }).reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0);
      let parent: Element | null = title;
      let background = 'rgb(255, 255, 255)';
      while (parent) {
        const candidate = getComputedStyle(parent).backgroundColor;
        if (candidate !== 'rgba(0, 0, 0, 0)' && candidate !== 'transparent') {
          background = candidate;
          break;
        }
        parent = parent.parentElement;
      }
      const foregroundLight = luminance(getComputedStyle(title).color);
      const backgroundLight = luminance(background);
      return (Math.max(foregroundLight, backgroundLight) + 0.05) / (Math.min(foregroundLight, backgroundLight) + 0.05);
    });
    expect(titleContrast, `Text contrast: ${await title.textContent()}`).toBeGreaterThanOrEqual(4.5);
  }
  for (const target of await page.locator('button:visible, a:visible, select:visible, input:visible').all()) {
    const bounds = await target.boundingBox();
    expect(bounds?.height, `Touch target: ${await target.textContent()}`).toBeGreaterThanOrEqual(44);
  }
}

test('collection grid and list preserve saved links across responsive layouts and transparency preferences', async ({ page, context }) => {
  // GIVEN several real saved holdings and a collection rendered in its default grid.
  await signIn(page);
  const names = ['Rose-cut garnet', 'Silver leaf pendant', 'Quartz specimen', 'Copper practice sheet'];
  for (const [index, name] of names.entries()) {
    await startItem(page, name);
    await page.getByLabel('Storage location (optional)', { exact: true }).fill(`Studio drawer ${index + 1}`);
    await page.getByRole('button', { name: 'Save item', exact: true }).click();
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(itemLink(page, name)).toBeVisible();
  }
  const grid = page.getByRole('button', { name: 'Grid', exact: true });
  const list = page.getByRole('button', { name: 'List', exact: true });
  await expect(grid).toHaveAttribute('aria-pressed', 'true');
  const originalLinks = await Promise.all(names.map(name => itemLink(page, name).getAttribute('href')));
  let reads = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/items' && request.method() === 'GET') reads++;
  });
  await mkdir(screenshotDirectory, { recursive: true });

  // WHEN each view is selected by keyboard at phone, tablet and desktop sizes in both themes.
  for (const width of [320, 390, 768, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const appearance of ['light', 'dark']) {
      await setAppearance(page, appearance === 'dark');
      for (const [view, button] of [['grid', grid], ['list', list]] as const) {
        await button.focus();
        await page.keyboard.press('Enter');
        await expect(button).toHaveAttribute('aria-pressed', 'true');
        await expect(button).toBeFocused();
        await inspectLayout(page);
        // THEN every saved item retains its link and no collection data is fetched again.
        expect(await Promise.all(names.map(name => itemLink(page, name).getAttribute('href')))).toEqual(originalLinks);
        expect(reads).toBe(0);
        const columns = new Set(await Promise.all(names.map(async name => Math.round((await itemLink(page, name).boundingBox())!.x))));
        if (view === 'list' || width === 320) expect(columns.size).toBe(1);
        if (view === 'grid' && width === 1440) expect(columns.size).toBeGreaterThan(1);
        await page.screenshot({ path: `${screenshotDirectory}/collection-${view}-${width}-${appearance}.png`, fullPage: true });
      }
    }
  }

  // AND reduced motion removes card travel, while enlarged text still reflows in the grid.
  await grid.click();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await itemLink(page, names[0]).hover();
  expect(await itemLink(page, names[0]).evaluate(element => getComputedStyle(element).transform)).toBe('none');
  expect(await itemLink(page, names[0]).evaluate(element => getComputedStyle(element).backgroundColor)).toMatch(/^rgb\(/);
  await page.setViewportSize({ width: 640, height: 1000 });
  const enlargedText = await page.addStyleTag({ content: 'html { font-size: 200%; }' });
  await inspectLayout(page);
  await enlargedText.evaluate(element => element.remove());
  await page.setViewportSize({ width: 1440, height: 1000 });

  // AND a reduced-transparency preference keeps the navigation opaque without changing item links.
  const cdp = await context.newCDPSession(page);
  try {
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
    expect(await page.evaluate(() => matchMedia('(prefers-reduced-transparency: reduce)').matches)).toBe(true);
    for (const appearance of ['light', 'dark']) {
      await setAppearance(page, appearance === 'dark');
      const navigation = page.locator('.workspace-nav');
      await expect.poll(() => navigation.evaluate(element => getComputedStyle(element).backdropFilter)).toBe('none');
      const background = await navigation.evaluate(element => getComputedStyle(element).backgroundColor);
      expect(background).toMatch(/^rgb\(/);
      await inspectLayout(page);
      await page.screenshot({ path: `${screenshotDirectory}/collection-opaque-nav-${appearance}.png`, fullPage: true });
    }
  } finally {
    await cdp.detach();
  }
  expect(reads).toBe(0);
  await itemLink(page, names[0]).click();
  await expect(page.getByRole('heading', { name: names[0], exact: true })).toBeVisible();
});

test('the studio shell reflows with enlarged text and respects reduced motion', async ({ page }) => {
  // GIVEN a collector entering a draft with the navigation and form surfaces.
  await signIn(page);
  await startItem(page, 'A sapphire to remember');
  await mkdir(screenshotDirectory, { recursive: true });
  for (const width of [390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const appearance of ['light', 'dark']) {
      // WHEN viewport and appearance change, the draft and accessible controls remain intact.
      await setAppearance(page, appearance === 'dark');
      await expect(page.getByLabel('Name', { exact: true })).toHaveValue('A sapphire to remember');
      await inspectLayout(page);
      await page.screenshot({ path: `${screenshotDirectory}/form-${width}-${appearance}.png`, fullPage: true });
    }
  }
  // THEN enlarged text reflows, and reduced motion removes pressed-control travel.
  await page.setViewportSize({ width: 640, height: 1000 });
  const enlargedText = await page.addStyleTag({ content: 'html { font-size: 200%; }' });
  await inspectLayout(page);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const save = page.getByRole('button', { name: 'Save item', exact: true });
  await save.focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  await expect(save).toBeFocused();
  expect(await save.evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe('none');
  expect(await save.evaluate(element => getComputedStyle(element).transitionDuration)).toBe('0s');
  await page.emulateMedia({ forcedColors: 'active' });
  expect(await save.evaluate(element => getComputedStyle(element).borderTopStyle)).toBe('solid');
  // AND shared account panels remain readable after their real data has loaded on a phone.
  await enlargedText.evaluate(element => element.remove());
  await page.emulateMedia({ forcedColors: 'none' });
  await page.setViewportSize({ width: 320, height: 900 });
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Discard changes', exact: true }).click();
  await openUserMenu(page);
  await page.getByRole('link', { name: 'Account', exact: true }).click();
  const sessionsTitle = page.getByRole('heading', { name: 'Sessions', exact: true });
  await expect(sessionsTitle).toBeVisible();
  await expect(page.getByText('Loading sessions…', { exact: true })).toBeHidden();
  expect(await sessionsTitle.evaluate(element => element.getBoundingClientRect().height <= parseFloat(getComputedStyle(element).lineHeight) + 1)).toBe(true);
  await inspectLayout(page);
  await page.screenshot({ path: `${screenshotDirectory}/account-320-dark.png`, fullPage: true });
  await page.getByRole('link', { name: 'Administration', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Tenant users', exact: true })).toBeVisible();
  await expect(page.getByText('Loading tenant users…', { exact: true })).toBeHidden();
  await inspectLayout(page);
  await page.screenshot({ path: `${screenshotDirectory}/administration-320-dark.png`, fullPage: true });
});

test('appearance survives authentication transitions when browser storage is blocked', async ({ page }) => {
  // GIVEN blocked storage and an explicit appearance selected on the public screen.
  await page.addInitScript(() => {
    Storage.prototype.getItem = () => { throw new Error('Storage blocked for this test'); };
    Storage.prototype.setItem = () => { throw new Error('Storage blocked for this test'); };
  });
  await page.goto('/');
  await setAppearance(page, true);
  // WHEN signing in and then out without reloading the document.
  await signInThroughUi(page, false);
  // THEN the in-memory choice remains intact in both control locations.
  await openUserMenu(page);
  await expect(page.getByRole('switch', { name: 'Dark theme' })).toBeChecked();
  await openUserMenu(page);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  await expect(page.getByRole('switch', { name: 'Dark theme' })).toBeChecked();
  expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe('dark');
});

async function startItem(page: Page, name: string) {
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
}

test('a real saved item survives reload and a separate authenticated browser session', async ({ page, browser }) => {
  // GIVEN a signed-in collector with an individually tracked piece.
  await signIn(page);
  const name = `Sapphire ${crypto.randomUUID()}`;
  const notes = 'Blue in daylight.\n<script>plain text only</script>\nKeep with certificate.';
  await startItem(page, name);
  await page.getByLabel('Notes (optional)', { exact: true }).fill(notes);
  await page.getByLabel('Storage location (optional)', { exact: true }).fill('Tray A, slot 3');

  // WHEN the user saves through the real API and reloads the resulting detail.
  const savedResponse = page.waitForResponse(response => response.url().endsWith('/api/items') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  expect((await savedResponse).status()).toBe(201);
  await expect(page).toHaveURL(/\/inventory\/[0-9a-f-]{36}$/);
  const detailUrl = page.url();
  const id = detailUrl.split('/').at(-1)!;
  await page.reload();

  // THEN every saved field and the complete permanent identifier remain readable as text.
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.getByText(notes, { exact: true })).toBeVisible();
  await expect(page.getByText('Tray A, slot 3', { exact: true })).toBeVisible();
  await expect(page.getByText(id, { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await itemLink(page, name).click();
  await expect(page).toHaveURL(detailUrl);

  // AND an independent cookie jar can sign in and reopen the same persisted record.
  const anotherSession = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const anotherPage = await anotherSession.newPage();
    await signIn(anotherPage, 'secondary');
    await itemLink(anotherPage, name).click();
    await expect(anotherPage).toHaveURL(detailUrl);
    await expect(anotherPage.getByText(notes, { exact: true })).toBeVisible();
  } finally {
    await anotherSession.close();
  }
});

test('a committed save with a lost response is explicitly retried without duplication', async ({ page }) => {
  // GIVEN the first POST commits on the real server but its response never reaches the client.
  await signIn(page);
  const name = `Uncertain save ${crypto.randomUUID()}`;
  const payloads: unknown[] = [];
  await page.route('**/api/items', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    payloads.push(route.request().postDataJSON());
    if (payloads.length === 1) {
      const response = await route.fetch();
      expect(response.status()).toBe(201);
      await route.abort('failed');
    } else {
      await route.continue();
    }
  });
  await startItem(page, name);

  // WHEN save becomes uncertain, editing is frozen until the user explicitly retries.
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeVisible();
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled();
  const replayResponse = page.waitForResponse(response => response.url().endsWith('/api/items') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Retry save', exact: true }).click();

  // THEN the server resolves the same request and the collection has exactly one entry.
  expect((await replayResponse).status()).toBe(200);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  expect(payloads).toHaveLength(2);
  expect(payloads[1]).toEqual(payloads[0]);
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await expect(itemLink(page, name)).toHaveCount(1);
});

test('dirty cancel, app navigation, history and sign-out require an explicit choice', async ({ page }) => {
  // GIVEN a dirty draft and a counter of actual creation requests.
  await signInThroughUi(page);
  let creates = 0;
  page.on('request', request => {
    if (request.url().endsWith('/api/items') && request.method() === 'POST') creates++;
  });
  await startItem(page, 'Unsaved stone');

  // WHEN cancel, navigation, browser back and sign-out are each declined.
  for (const leave of [
    () => page.getByRole('button', { name: 'Cancel', exact: true }).click(),
    async () => { await openUserMenu(page); await page.getByRole('link', { name: 'Account', exact: true }).click(); },
    () => page.goBack(),
    async () => { await openUserMenu(page); await page.getByRole('button', { name: 'Sign out', exact: true }).click(); },
  ]) {
    await leave();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Keep editing', exact: true }).click();
    // THEN the draft remains intact and nothing has been created.
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Unsaved stone');
    await expect(page).toHaveURL(/\/inventory\/new$/);
    expect(creates).toBe(0);
  }

  // WHEN the user confirms sign-out, the protected draft is discarded.
  await openUserMenu(page);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Discard changes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
  await signInThroughUi(page);
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await expect(page.getByLabel('Name', { exact: true })).toBeEmpty();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
  expect(creates).toBe(0);
});

test('skip-link fragment navigation preserves the dirty draft guard across browser back', async ({ page }) => {
  // GIVEN a dirty draft reached through app navigation.
  await signIn(page);
  await startItem(page, 'Draft after skip navigation');

  // WHEN keyboard skip navigation adds a fragment, account navigation still requires consent.
  await page.getByRole('link', { name: 'Skip to content', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/inventory\/new#main$/);
  await openUserMenu(page);
  await page.getByRole('link', { name: 'Account', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Draft after skip navigation');

  // AND browser back removes only the fragment, keeping the same protected draft.
  await page.goBack();
  await expect(page).toHaveURL(/\/inventory\/new$/);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();

  // THEN cancel still prompts instead of silently dropping the draft.
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Draft after skip navigation');
});

test('collection failure has a retry state distinct from an empty collection', async ({ page }) => {
  // GIVEN a collection endpoint that fails while authentication remains available.
  let fail = true;
  await page.route('**/api/items', route => route.request().method() === 'GET'
    ? route.fulfill(fail
      ? { status: 503, json: { title: 'Collection unavailable', status: 503 } }
      : { json: { items: [], nextCursor: null } })
    : route.continue());
  await signIn(page);
  await expect(page.getByRole('alert')).toBeVisible();
  const failureText = await page.getByRole('alert').innerText();

  // WHEN the endpoint recovers with a genuinely empty result and the user retries.
  fail = false;
  await page.getByRole('button', { name: /retry/i }).click();

  // THEN the error disappears and the empty collection offers its creation action.
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.getByText(failureText, { exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Add item', exact: true })).toBeVisible();
  await expect(page.getByText(/no items|collection is empty|first item/i)).toBeVisible();
});

for (const width of [320, 1280]) {
  test(`collection creation and detail remain usable at ${width}px in both appearances`, async ({ page }) => {
    // GIVEN a narrow or desktop viewport and a signed-in collector.
    await page.setViewportSize({ width, height: 900 });
    await signIn(page);
    const name = `Layout ${width} ${crypto.randomUUID()}`;
    await startItem(page, name);
    const notes = 'A'.repeat(240) + '\nSecond line';
    await page.getByLabel('Notes (optional)', { exact: true }).fill(notes);

    // WHEN appearance changes on a dirty form, entered content remains available.
    for (const appearance of ['Dark', 'Light']) {
      await setAppearance(page, appearance.toLowerCase() === 'dark');
      await expect(page.getByLabel('Name', { exact: true })).toHaveValue(name);
      await expect(page.getByLabel('Notes (optional)', { exact: true })).toHaveValue(notes);
      await expect.poll(() => page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe(appearance.toLowerCase());
      await inspectLayout(page);
    }

    // AND keyboard submission creates the record, including long unbroken text.
    await page.getByRole('button', { name: 'Save item', exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    for (const appearance of ['Dark', 'Light']) {
      await setAppearance(page, appearance.toLowerCase() === 'dark');
      // THEN the entire identifier and notes fit without horizontal page overflow.
      await expect(page.getByText(notes, { exact: true })).toBeVisible();
      await inspectLayout(page);
      if ((width === 320 && appearance === 'Light') || (width === 1280 && appearance === 'Dark')) {
        await mkdir(screenshotDirectory, { recursive: true });
        await page.screenshot({ path: `${screenshotDirectory}/detail-${width}-${appearance.toLowerCase()}.png`, fullPage: true });
        await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
        await expect(itemLink(page, name)).toBeVisible();
        await itemLink(page, name).hover();
        await inspectLayout(page);
        await page.screenshot({ path: `${screenshotDirectory}/collection-${width}-${appearance.toLowerCase()}.png`, fullPage: true });
        await itemLink(page, name).click();
        await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
      }
    }
    await page.reload();
    await openUserMenu(page);
    await expect(page.getByRole('switch', { name: 'Dark theme' })).not.toBeChecked();
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  });
}
