import { expect, test } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 1440]) test(`purchasing controls and compact supplier selection remain coherent at ${width}px`, async ({ page }) => {
  // GIVEN a reusable supplier and the purchasing workspace at a supported narrow or desktop size.
  await signIn(page);
  await page.setViewportSize({ width, height: 900 });
  const supplier = `Polish sample ${width} ${Date.now()}`;
  await page.goto('/suppliers/new');
  await page.getByLabel('Name', { exact: true }).fill(supplier);
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
  expect(await dialog.getByLabel('Name', { exact: true }).evaluate(el => getComputedStyle(el.closest('form')!).borderTopWidth)).toBe('0px');
  await dialog.getByLabel('Name', { exact: true }).fill('Keep these edits');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('button', { name: 'Keep editing supplier', exact: true }).click();
  await expect(dialog.getByLabel('Name', { exact: true })).toHaveValue('Keep these edits');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
