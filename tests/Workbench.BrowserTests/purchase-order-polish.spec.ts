import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 1440]) test(`purchasing controls and compact supplier selection remain coherent at ${width}px`, async ({ page }) => {
  // GIVEN a reusable supplier and the purchasing workspace at a supported narrow or desktop size.
  await signIn(page);
  await page.setViewportSize({ width, height: 900 });
  const supplier = `Polish sample ${width} ${Date.now()}`;
  await page.goto('/suppliers/new');
  await page.getByLabel('Supplier name', { exact: true }).fill(supplier);
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Archive supplier', exact: true })).toBeEnabled();
  await page.goto('/suppliers');
  const edit = page.getByRole('link', { name: `Edit ${supplier}`, exact: true });
  await expect(edit).toBeVisible();
  expect((await edit.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  const filter = page.getByLabel('Include archived suppliers');
  expect(await filter.evaluate(el => getComputedStyle(el.parentElement!).display)).toBe('flex');
  const search = page.getByLabel('Search suppliers', { exact: true });
  // WHEN focusing search THEN the shared field has one border and no competing outer focus ring.
  await search.focus();
  await expect(search).toHaveCSS('border-top-width', '2px');
  await expect(search).toHaveCSS('outline-style', 'none');
  await page.getByRole('link', { name: 'Back to purchase orders', exact: true }).click();
  expect((await page.getByRole('link', { name: 'Manage suppliers' }).boundingBox())!.height).toBeGreaterThanOrEqual(44);
  await page.getByRole('link', { name: 'New draft', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'New purchase order', exact: true })).toBeVisible();
  await expect(page.getByText('Draft', { exact: true })).toHaveCount(1);
  // WHEN selecting a supplier for an empty order THEN only populated details are confirmed, without a blank comparison.
  await page.getByRole('button', { name: 'Choose supplier', exact: true }).click();
  await page.getByLabel('Search suppliers', { exact: true }).fill(supplier);
  await page.getByRole('button', { name: `Select ${supplier}`, exact: true }).click();
  const confirmation = page.getByRole('dialog', { name: 'Use supplier?', exact: true });
  await expect(confirmation).toContainText(supplier);
  await expect(confirmation).not.toContainText('Not set');
  await page.keyboard.press('Escape');
  await expect(page.getByLabel('Supplier name', { exact: true })).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Choose supplier', exact: true })).toBeFocused();
  // WHEN creating another supplier inline THEN the form has a shared footer and preserves unsaved input on cancel.
  await page.getByRole('button', { name: 'New supplier', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New supplier', exact: true });
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toHaveCount(1);
  expect(await dialog.getByLabel('Supplier name', { exact: true }).evaluate(el => getComputedStyle(el.closest('form')!).borderTopWidth)).toBe('0px');
  await dialog.getByLabel('Supplier name', { exact: true }).fill('Keep these edits');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Keep editing supplier', exact: true }).click();
  await expect(dialog.getByLabel('Supplier name', { exact: true })).toHaveValue('Keep these edits');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('saved order lines stay compact and reopen by keyboard with precise quantities and sticky save feedback', async ({ page }) => {
  // GIVEN a new line whose quantities and pricing basis have not been inferred.
  await signIn(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Compact lines ${Date.now()}`);
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  const disclosure = page.locator('.po-line-disclosure').first();
  const summary = disclosure.locator(':scope > summary');
  const quantity = page.getByLabel('Quantity 1', { exact: true });
  await expect(disclosure).toHaveAttribute('open', '');
  await expect(page.getByLabel('Description 1', { exact: true })).toBeFocused();
  await expect(quantity).toHaveValue('');
  await page.getByLabel('Description 1', { exact: true }).fill('Blue sapphires');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('carat');

  // WHEN entering the supplier quantity THEN typing preserves the entered magnitudes.
  await quantity.pressSequentially('12.5');
  await expect(quantity).toHaveValue('12.5');
  await page.getByLabel('Unit price 1', { exact: true }).pressSequentially('2000');
  await expect(page.getByLabel('Unit price 1', { exact: true })).toHaveValue('20.00');
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 250.00');
  const saved = page.waitForResponse(response => /\/api\/v4\/purchase-order-drafts$/.test(response.url()) && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await saved).ok()).toBe(true);
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.reload();

  // THEN the saved line has a readable summary while its editing controls remain collapsed.
  await expect(disclosure).not.toHaveAttribute('open', '');
  await expect(summary).toHaveAccessibleName('Edit line 1: Blue sapphires');
  await expect(summary).toContainText('Blue sapphires');
  await expect(summary).toContainText('12.5 carats');
  await expect(summary).toContainText('USD 250.00');
  await expect(quantity).not.toBeVisible();
  const toolbar = page.locator('.po-editor-toolbar');
  await expect(toolbar.getByRole('status')).toHaveText('Saved');

  // WHEN expanding with Enter THEN stored values show only significant quantity digits.
  await summary.focus();
  await summary.press('Enter');
  await expect(quantity).toBeVisible();
  await expect(quantity).toHaveValue('12.5');
  await expect(page.getByLabel('Unit price 1', { exact: true })).toHaveValue('20.00');
  await quantity.fill('251');

  // THEN unsaved feedback remains beside Save even after scrolling into the line editor.
  await expect(toolbar.getByRole('status')).toHaveText('Unsaved changes');
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  await expect(toolbar.getByRole('status')).toBeInViewport();
  await expect(toolbar.getByRole('button', { name: 'Save draft', exact: true })).toBeInViewport();
  await summary.focus();
  await summary.press('Space');
  await expect(quantity).not.toBeVisible();
});
