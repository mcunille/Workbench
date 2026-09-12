import { expect, test, type Page } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';
import { setAppearance } from './user-menu-fixture';
import { browserBaseUrl } from './browser-environment';

test.setTimeout(120_000);

async function startDraft(page: Page, title: string) {
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(title);
}

async function save(page: Page) {
  const response = page.waitForResponse(r => r.url().includes('/api/purchase-order-drafts') &&
    ['POST', 'PUT'].includes(r.request().method()));
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await response).ok()).toBe(true);
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
}

test('incomplete shopping list survives reload and another session with unknown and zero prices distinct', async ({ page, browser }) => {
  // GIVEN a signed-in owner planning a purchase with one unknown price and one explicit zero.
  await signIn(page);
  const title = `September draft ${Date.now()}`;
  await startDraft(page, title);
  await page.getByLabel('Supplier name', { exact: true }).fill('Sample supplier');
  await page.getByLabel('Notes', { exact: true }).fill('Ask about shipping before ordering.');
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  await expect(page.getByLabel('Description 1', { exact: true })).toBeFocused();
  await expect(page.getByLabel('Description 1', { exact: true })).toBeInViewport();
  await page.getByLabel('Description 1', { exact: true }).fill('Blue sapphires');
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  await page.getByLabel('Description 2', { exact: true }).fill('Sample setting');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByLabel('Reference price 2', { exact: true }).fill('0');

  // WHEN the owner saves and reloads the order.
  await save(page);
  const path = new URL(page.url()).pathname;
  await page.reload();

  // THEN incomplete content persists and unknown is not converted to zero.
  await expect(page.getByLabel('Description 1', { exact: true })).toHaveValue('Blue sapphires');
  await expect(page.getByLabel('Reference price 1', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Reference price 2', { exact: true })).toHaveValue('0.00');
  await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('Ask about shipping before ordering.');

  // WHEN clearing is requested THEN Cancel receives focus and Escape preserves prices.
  await page.getByRole('button', { name: 'Clear all reference prices' }).click();
  await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByLabel('Reference price 2', { exact: true })).toHaveValue('0.00');
  await expect(page.getByRole('button', { name: 'Clear all reference prices' })).toBeFocused();

  // AND a distinct authenticated session can resume the same saved draft.
  const otherContext = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const other = await otherContext.newPage();
    await signIn(other, 'secondary');
    await other.goto(path);
    await expect(other.getByLabel('Title', { exact: true })).toHaveValue(title);
  } finally { await otherContext.close(); }

  // WHEN the toolbar pins over content THEN it gains an opaque background and clears again at the top.
  await page.evaluate(() => window.scrollTo(0, 0));
  const toolbar = page.locator('.po-editor-toolbar');
  await expect(toolbar).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
  await page.evaluate(() => window.scrollTo(0, 500));
  await expect(toolbar).not.toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect(toolbar).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');

  // AND the editor stays usable at a phone width in both appearances.
  await page.setViewportSize({ width: 390, height: 844 });
  for (const dark of [false, true]) {
    await setAppearance(page, dark);
    await expect(page.getByLabel('Title', { exact: true })).toHaveValue(title);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
});

test('uncertain creation retries the original request without creating another draft', async ({ page }) => {
  // GIVEN the server saves a draft but its acknowledgement is lost.
  await signIn(page);
  const title = `Retry draft ${Date.now()}`;
  await startDraft(page, title);
  const requests: unknown[] = [];
  let dropped = false;
  await page.route('**/api/purchase-order-drafts', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    requests.push(route.request().postDataJSON());
    const response = await route.fetch();
    if (!dropped) { dropped = true; await route.abort('failed'); }
    else await route.fulfill({ response });
  });

  // WHEN the owner checks and retries the uncertain save.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await page.getByRole('button', { name: 'Check and retry', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();

  // THEN the same complete request was retried and the business has only one matching draft.
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  const response = await page.request.get('/api/purchase-order-drafts');
  expect(response.ok()).toBe(true);
  const body = await response.json();
  expect(body.items.filter((item: { title: string }) => item.title === title)).toHaveLength(1);
});

test('competing edits preserve local input until the owner deliberately reconciles', async ({ page, browser }) => {
  // GIVEN two sessions opened the same saved draft version.
  await signIn(page);
  await startDraft(page, `Concurrent draft ${Date.now()}`);
  await save(page);
  const path = new URL(page.url()).pathname;
  const otherContext = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const other = await otherContext.newPage();
    await signIn(other, 'secondary');
    await other.goto(path);
    await other.getByLabel('Notes', { exact: true }).fill('My local supplier questions');
    await page.getByLabel('Notes', { exact: true }).fill('Supplier confirmed availability');
    await save(page);

    // WHEN the second owner attempts a stale save.
    await other.getByRole('button', { name: 'Save draft', exact: true }).click();

    // THEN both versions remain available and no automatic overwrite occurs.
    const comparison = other.getByLabel('Compare draft versions');
    await expect(comparison.getByText('Supplier confirmed availability', { exact: true })).toBeVisible();
    await expect(comparison.getByText('My local supplier questions', { exact: true })).toBeVisible();
    await other.getByRole('button', { name: 'Continue with my changes', exact: true }).click();
    await expect(other.getByLabel('Notes', { exact: true })).toHaveValue('My local supplier questions');
    await save(other);
    await page.reload();
    await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('My local supplier questions');
  } finally { await otherContext.close(); }
});

test('a confirmed save retries only the failed current-details read', async ({ page }) => {
  // GIVEN the server confirms creation but the first current-details read is unavailable.
  await signIn(page);
  await startDraft(page, `Read recovery ${Date.now()}`);
  let saves = 0;
  let reads = 0;
  await page.route('**/api/purchase-order-drafts**', async route => {
    if (route.request().method() === 'POST') saves++;
    if (route.request().method() === 'GET' && /\/api\/purchase-order-drafts\/[a-f0-9-]{36}$/.test(route.request().url())) {
      reads++;
      if (reads === 1) return route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{"status":503}' });
    }
    await route.continue();
  });

  // WHEN the owner retries loading the acknowledged saved draft.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByText('Saved; current version could not be loaded.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Load current draft', exact: true }).click();

  // THEN the editor resumes without another creation or mutation request.
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
  expect(saves).toBe(1);
  expect(reads).toBe(2);
});

test('reference prices shift cents by default and retain opt-in extra precision after saving', async ({ page }) => {
  // GIVEN an unknown reference price on a new draft.
  await signIn(page);
  await startDraft(page, 'Price entry check');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  const price = page.getByLabel('Reference price 1', { exact: true });
  await expect(price).toHaveValue('');
  await expect(price).toHaveAttribute('placeholder', '0.00');
  // WHEN typing digits THEN they shift from hundredths to whole units.
  await price.pressSequentially('1'); await expect(price).toHaveValue('0.01');
  await price.pressSequentially('2'); await expect(price).toHaveValue('0.12');
  await price.pressSequentially('3'); await expect(price).toHaveValue('1.23');
  await price.pressSequentially('4'); await expect(price).toHaveValue('12.34');
  await price.press('ControlOrMeta+a'); await price.press('Backspace');
  await expect(price).toHaveValue('');
  // WHEN opting into extra precision and saving THEN meaningful digits survive reload.
  await page.getByRole('checkbox', { name: 'Use extra precision for entry 1' }).check();
  await price.fill('0.0123');
  await save(page);
  await page.reload();
  await expect(price).toHaveValue('0.0123');
  await expect(page.getByRole('checkbox', { name: 'Use extra precision for entry 1' })).toBeChecked();
});
