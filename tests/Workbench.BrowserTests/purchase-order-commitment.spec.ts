import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test.setTimeout(120_000);

function comparison(page: Page, heading: string) {
  return page.locator('.po-comparison-content').filter({
    has: page.getByRole('heading', { name: heading, exact: true }),
  });
}

test('an ordered purchase preserves unknown agreed costs and its original revision after amendment', async ({ page }) => {
  // GIVEN an owner has saved valid quantities with an unknown stone price and estimated shipping.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  const title = `PO-04 agreement ${Date.now()}`;
  await page.getByLabel('Title', { exact: true }).fill(title);
  await page.getByLabel('Supplier name', { exact: true }).fill('Original gemstone supplier');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Blue sapphires');
  await page.getByLabel('Quantity 1', { exact: true }).fill('2');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByRole('button', { name: 'Add charge', exact: true }).click();
  await page.getByLabel('Category 1', { exact: true }).selectOption('shipping');
  await page.getByLabel('Charge amount 1', { exact: true }).fill('500');
  await page.getByLabel('Amount status 1', { exact: true }).selectOption('estimated');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/PO-/);
  const reference = await page.getByRole('heading', { level: 1 }).innerText();
  const orderPath = new URL(page.url()).pathname;

  // WHEN the owner records the saved purchase as ordered on an explicit calendar date.
  await page.getByRole('button', { name: 'Record as ordered', exact: true }).click();
  await page.getByLabel('Order date', { exact: true }).fill('2026-09-12');
  await page.getByRole('button', { name: 'Confirm order', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  await page.reload();

  // THEN the persisted agreement is ordered and cannot use draft editing or deletion.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(reference);
  await expect(page.locator('time[datetime="2026-09-12"]')).toHaveText('2026-09-12');
  await expect(page.getByText('Ordered', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Delete draft', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Clear all amounts', exact: true })).toHaveCount(0);
  await expect(page.getByRole('textbox', { name: 'Quantity 1', exact: true })).toHaveCount(0);
  await expect(page.getByText('Supplier estimate', { exact: true })).toBeVisible();
  await expect(page.getByText('Total purchase estimate', { exact: true })).toBeVisible();

  // AND the first revision shows the original supplier, quantity and unresolved unit price.
  await page.getByRole('button', { name: 'View history', exact: true }).click();
  await page.getByRole('button', { name: 'View revision 1', exact: true }).click();
  const original = comparison(page, 'Revision 1');
  await expect(original).toContainText('Original gemstone supplier');
  await expect(original).toContainText('Blue sapphires');
  await expect(original.locator('div').filter({ has: page.locator('dt').getByText('Quantity', { exact: true }) })).toContainText('2 pieces');
  await expect(original.locator('div').filter({ has: page.locator('dt').getByText('Unit price', { exact: true }) })).toContainText('Unknown');
  await expect(original).toContainText('Estimated');
  const agreedContents = await original.innerText();
  const commitmentHistory = page.locator('.po-history-list li').filter({ has: page.getByRole('button', { name: 'View revision 1', exact: true }) });
  const originalActor = await commitmentHistory.locator('.po-history-actor').innerText();
  const originalRecordedAt = await commitmentHistory.locator('time').getAttribute('datetime');
  expect(originalActor).toMatch(/Recorded by [a-f0-9-]{36}/i);
  expect(originalRecordedAt).toBeTruthy();

  // WHEN the owner amends the supplier, quantity and price with an explanation.
  await page.goto(orderPath);
  await page.getByRole('button', { name: 'Create amendment', exact: true }).click();
  await expect(page.getByLabel('Currency', { exact: true })).toBeDisabled();
  await page.locator('.po-supplier-summary').click();
  await page.getByLabel('Supplier name', { exact: true }).fill('Corrected gemstone supplier');
  await page.locator('.po-line-disclosure > summary').first().click();
  await page.getByLabel('Quantity 1', { exact: true }).fill('3');
  await page.getByLabel('Unit price 1', { exact: true }).fill('2000');
  await page.getByLabel('Amendment reason', { exact: true }).fill('Supplier confirmed three stones and corrected their trading name.');
  await page.getByRole('button', { name: 'Review amendment', exact: true }).click();
  await expect(page.getByText('Original gemstone supplier', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Corrected gemstone supplier', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Record amendment', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  await page.reload();

  // THEN the latest purchase estimate reflects the amendment and remains usable on a phone.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(reference);
  await expect(page.locator('time[datetime="2026-09-12"]')).toHaveText('2026-09-12');
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 65.00');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'View history', exact: true }).click();
  await page.getByRole('button', { name: 'View revision 2', exact: true }).click();
  const amended = comparison(page, 'Revision 2');
  await expect(amended).toContainText('Corrected gemstone supplier');
  await expect(amended.locator('div').filter({ has: page.locator('dt').getByText('Quantity', { exact: true }) })).toContainText('3 pieces');
  await expect(amended.locator('div').filter({ has: page.locator('dt').getByText('Unit price', { exact: true }) })).toContainText('20.00');
  await expect(page.getByRole('region', { name: 'Order history', exact: true })).toContainText('Supplier confirmed three stones and corrected their trading name.');

  // AND inspecting the earlier revision after reload retains exactly the originally agreed contents.
  await page.getByRole('button', { name: 'View revision 1', exact: true }).click();
  await expect(comparison(page, 'Revision 1')).toHaveText(agreedContents, { useInnerText: true });
  await expect(commitmentHistory.locator('.po-history-actor')).toHaveText(originalActor);
  await expect(commitmentHistory.locator('time')).toHaveAttribute('datetime', originalRecordedAt!);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
