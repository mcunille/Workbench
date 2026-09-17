import { test, expect } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { setAppearance } from './user-menu-fixture';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';

test('discounts and source charges reconcile and persist without combining supplier and bank costs', async ({ page }) => {
  // GIVEN the scenario's stones and settings entered as a draft purchase.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill('PO-05 worked example');
  await page.getByLabel('Supplier name', { exact: true }).fill('Sample gemstone supplier');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  for (const [index, description, quantity, price] of [[1, 'Stones', '10', '2000'], [2, 'Settings', '20', '500']] as const) {
    await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
    await page.getByLabel(`Description ${index}`, { exact: true }).fill(description);
    await page.getByLabel(`Quantity ${index}`, { exact: true }).fill(quantity);
    await page.getByLabel(`Unit ${index}`, { exact: true }).selectOption('piece');
    await page.getByLabel(`Unit price ${index}`, { exact: true }).fill(price);
  }
  // WHEN line and order discounts precede separately entered shipping, sales tax and a bank fee.
  await page.getByRole('button', { name: 'Add line discount 1', exact: true }).click();
  await page.getByLabel('Line discount 1 type', { exact: true }).selectOption('percentage');
  await page.getByLabel('Line discount 1 percentage', { exact: true }).fill('10');
  await page.getByRole('button', { name: 'Add order discount', exact: true }).click();
  await page.getByLabel('Order discount amount', { exact: true }).fill('1000');
  for (const [index, category, amount] of [[1, 'shipping', '1500'], [2, 'salesTax', '2160'], [3, 'paymentFee', '300']] as const) {
    await page.getByRole('button', { name: 'Add charge', exact: true }).click();
    await page.getByLabel(`Category ${index}`, { exact: true }).selectOption(category);
    await page.getByLabel(`Charge amount ${index}`, { exact: true }).fill(amount);
    await page.getByLabel(`Amount status ${index}`, { exact: true }).selectOption('confirmed');
  }
  await page.getByLabel('Payee 3', { exact: true }).selectOption('thirdParty');
  await page.getByLabel('Payee name 3', { exact: true }).fill('Sample bank');
  // THEN supplier and whole-purchase estimates reconcile independently with inspectable bases.
  await expect(page.locator('.po-summary-subtotal dd')).toHaveText('USD 306.60');
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 309.60');
  await expect(page.locator('.po-discount').last()).toContainText('Eligible base: USD 280.00');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.reload();
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 309.60');
  await expect(page.getByLabel('Payee name 3', { exact: true })).toHaveValue('Sample bank');
  await expect(page.locator('.po-line-disclosure > summary').first()).toContainText('USD 180.00');

  // AND both appearances and supported screen sizes retain accessible controls without overflow.
  const evidence = process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY;
  if (evidence) await mkdir(evidence, { recursive: true });
  for (const appearance of ['light', 'dark'] as const) {
    await setAppearance(page, appearance === 'dark');
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 960 });
      await page.evaluate(() => window.scrollTo(0, 0));
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (evidence) await page.screenshot({ path: path.join(evidence, `po05-${appearance}-${width}.png`), fullPage: true });
    }
  }
  await page.setViewportSize({ width: 320, height: 960 });
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });

  // WHEN a confirmed amount changes THEN the save preserves edits until a correction explanation is supplied.
  const shipping = page.getByLabel('Charge amount 1', { exact: true });
  await shipping.focus();
  await shipping.press('ControlOrMeta+A');
  await shipping.press('Backspace');
  await shipping.pressSequentially('1600');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('explanation');
  await expect(page.getByLabel('Charge amount 1', { exact: true })).toHaveValue('16.00');
  await page.getByLabel('Charge notes 1', { exact: true }).fill('Updated shipping quote from supplier.');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 310.60');
});
